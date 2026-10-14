// recast_cli - native Linux replacement for the tweaked Windows RecastDemo.exe
// used by OpenDAoC-BuildNav (World/NavmeshMgr.cs RunRecast).
//
//   usage: recast_cli <in.gset> <out.nav>
//
// Reproduces the hardcoded parameters of that binary, reverse-engineered with
// Ghidra from base/RecastDemo.exe (see .scratch/buildnav.log):
//   - CLI entry FUN_140021d20 -> factory PTR_FUN_140070088 = FUN_140021cf0
//     -> "new Sample_TileMesh" (ctor FUN_1400167c0, base ctor FUN_14000fee0)
//   - Sample::Sample defaults: cellSize=0.1 cellHeight=0.1 agentHeight=1.8
//     agentRadius=0.3 agentMaxClimb=0.9 agentMaxSlope=80 regionMinSize=8
//     regionMergeSize=96 edgeMaxLen=12 edgeMaxError=1.3 vertsPerPoly=6
//     detailSampleDist=4 detailSampleMaxError=1, partitionType=WATERSHED (+0x60)
//   - Sample_TileMesh ctor: tileSize=96 (handleMeshChanged only overrides it
//     when the gset carries an 's' settings line; BuildNav never writes one)
//   - filterLowHangingObstacles / filterLedgeSpans / filterWalkableLowHeightSpans
//     all true (ctor writes 0x101 at +0x64, 1 at +0x66)
//   - dtNavMeshParams: orig=mesh bmin, cs=ch=tileSize*cellSize=9.6,
//     maxTiles=65536, maxPolys=1048576 (matches shipped zone000.nav header)
//   - input: .gset text format ('f' obj path, 'v' convex volumes, 'c' off-mesh
//     connections, optional 's' settings - parsed but unused), MAX_OFFMESH_
//     CONNECTIONS 4096 (InputGeom tweak, matches NavmeshMgr.RecastOffMeshConnectionLimit)
//   - output: NAVMESHSET file (magic 'MSET' 0x4d534554, version 1) exactly as
//     the server's Detour/Source/DetourInterface.cpp LoadNavMesh expects,
//     including the 4 bytes of struct padding after each tile size.
//
// Built against upstream recastnavigation Recast + this repo's OpenDAoC-Core
// Detour fork compiled with DT_POLYREF64 (64-bit poly refs, same as the exe).

#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

#include "ChunkyTriMesh.h"
#include "DetourNavMesh.h"
#include "DetourNavMeshBuilder.h"
#include "DetourNavMeshQuery.h"
#include "Recast.h"

// ---------------------------------------------------------------------------
// Hardcoded build configuration (see header comment).
// ---------------------------------------------------------------------------
namespace
{
constexpr float kCellSize = 0.1f;
constexpr float kCellHeight = 0.1f;
constexpr float kAgentHeight = 1.8f;
constexpr float kAgentRadius = 0.3f;
constexpr float kAgentMaxClimb = 0.9f;
constexpr float kAgentMaxSlope = 80.0f;
constexpr float kRegionMinSize = 8.0f;
constexpr float kRegionMergeSize = 96.0f;
constexpr float kEdgeMaxLen = 12.0f;
constexpr float kEdgeMaxError = 1.3f;
constexpr float kVertsPerPoly = 6.0f;
constexpr float kDetailSampleDist = 4.0f;
constexpr float kDetailSampleMaxError = 1.0f;
constexpr int kTileSize = 96;                       // voxels
constexpr int kMaxTiles = 65536;                    // "handle large worlds" tweak
constexpr int kMaxPolys = 1048576;
constexpr int kMaxOffMeshConnections = 4096;        // InputGeom.h tweak
constexpr int kMaxNavQueryNodes = 0x800;            // FUN_140035960(ctx, mesh, 0x800)
constexpr int kTrisPerChunk = 256;                  // chunky tri mesh (FUN_1400020d0)

// DAoC gset enums (CEM GeomSetWriter.eAreas / eFlags).
enum PolyAreas
{
    AREA_GROUND = 0,
    AREA_WATER = 1,
    AREA_ROAD = 2,
    AREA_DOOR = 3,
    AREA_GRASS = 4,
    AREA_JUMP = 5,
};

enum PolyFlags
{
    FLAG_WALK = 0x01,
    FLAG_SWIM = 0x02,
    FLAG_DOOR = 0x04,
    FLAG_JUMP = 0x08,
};

// ---------------------------------------------------------------------------
struct ConvexVolume
{
    float verts[16 * 3];
    int nverts = 0;
    float hmin = 0.f, hmax = 0.f;
    unsigned char area = AREA_GROUND;
};

struct GeomSet
{
    std::vector<float> verts;
    std::vector<int> tris;
    std::vector<ConvexVolume> volumes;
    std::vector<float> omcVerts;      // 2 * count * 3
    std::vector<float> omcRadius;
    std::vector<unsigned char> omcBidir;
    std::vector<unsigned char> omcArea;
    std::vector<unsigned short> omcFlags;
    float bmin[3] = {0, 0, 0};
    float bmax[3] = {0, 0, 0};
};

bool loadObj(const std::string &path, GeomSet &geom)
{
    FILE *fp = fopen(path.c_str(), "rb");
    if (!fp)
        return false;

    char *buf = nullptr;
    size_t len = 0, cap = 0;
    for (;;)
    {
        if (len + 65536 > cap)
        {
            cap = cap ? cap * 2 : 1 << 20;
            buf = (char *)realloc(buf, cap);
        }
        size_t got = fread(buf + len, 1, 65536, fp);
        len += got;
        if (got < 65536)
            break;
    }
    fclose(fp);

    std::vector<float> v;
    std::vector<int> f;
    int vertexFlags[3]; // scratch: t/m values per vertex line, ignored
    (void)vertexFlags;

    char *p = buf, *end = buf + len;
    char line[4096];
    while (p < end)
    {
        size_t n = 0;
        while (p < end && *p != '\n' && n < sizeof(line) - 1)
            line[n++] = *p++;
        if (p < end && *p == '\n')
            ++p;
        line[n] = 0;

        if (line[0] == 'v' && (line[1] == ' ' || line[1] == '\t'))
        {
            float x, y, z;
            if (sscanf(line + 2, "%f %f %f", &x, &y, &z) == 3)
            {
                v.push_back(x);
                v.push_back(y);
                v.push_back(z);
            }
        }
        else if (line[0] == 'f' && (line[1] == ' ' || line[1] == '\t'))
        {
            int vi[64];
            int nv = 0;
            const char *q = line + 2;
            while (*q && nv < 64)
            {
                while (*q == ' ' || *q == '\t')
                    ++q;
                if (!*q)
                    break;
                int idx = 0;
                bool neg = false;
                if (*q == '-')
                {
                    neg = true;
                    ++q;
                }
                bool any = false;
                while (*q >= '0' && *q <= '9')
                {
                    idx = idx * 10 + (*q - '0');
                    ++q;
                    any = true;
                }
                if (!any)
                {
                    // skip a token without leading integer (e.g. "f //")
                    while (*q && *q != ' ' && *q != '\t')
                        ++q;
                    continue;
                }
                idx = neg ? -idx : idx;
                while (*q && *q != ' ' && *q != '\t')
                    ++q; // skip /vt/vn
                // 1-based index, negative = relative
                int real = idx > 0 ? idx - 1 : (int)v.size() / 3 + idx;
                if (real < 0 || real >= (int)v.size() / 3)
                {
                    free(buf);
                    fprintf(stderr, "recast_cli: face index out of range in %s\n", path.c_str());
                    return false;
                }
                vi[nv++] = real;
            }
            // fan triangulate
            for (int i = 2; i < nv; ++i)
            {
                f.push_back(vi[0]);
                f.push_back(vi[i - 1]);
                f.push_back(vi[i]);
            }
        }
    }
    free(buf);

    if (v.empty())
    {
        fprintf(stderr, "recast_cli: no vertices in %s\n", path.c_str());
        return false;
    }

    geom.verts = v;
    geom.tris = f;
    rcCalcBounds(geom.verts.data(), (int)geom.verts.size() / 3, geom.bmin, geom.bmax);
    return true;
}

// Parse a .gset (see CEM Client/Writers/GeomSetWriter.cs and the demo's
// loadGeomSet FUN_140009130).
bool loadGset(const std::string &path, GeomSet &geom)
{
    FILE *fp = fopen(path.c_str(), "rb");
    if (!fp)
    {
        fprintf(stderr, "recast_cli: cannot open gset '%s'\n", path.c_str());
        return false;
    }

    bool ok = false;
    char line[4096];
    while (fgets(line, sizeof(line), fp))
    {
        // tokenize on whitespace like the demo does
        char *tok[32];
        int ntok = 0;
        for (char *q = strtok(line, " \t\r\n"); q && ntok < 32; q = strtok(nullptr, " \t\r\n"))
            tok[ntok++] = q;
        if (ntok == 0)
            continue;

        if (tok[0][0] == 'f' && tok[0][1] == 0 && ntok >= 2)
        {
            std::string objPath = tok[1];
            if (!loadObj(objPath, geom))
            {
                // fall back: relative to the gset's directory
                std::string dir = path.substr(0, path.find_last_of("/\\"));
                if (!dir.empty())
                {
                    std::string alt = dir + "/" + objPath;
                    if (loadObj(alt, geom))
                        objPath = alt;
                    else
                    {
                        fprintf(stderr, "recast_cli: cannot load obj '%s'\n", objPath.c_str());
                        fclose(fp);
                        return false;
                    }
                }
                else
                {
                    fclose(fp);
                    return false;
                }
            }
            ok = true;
        }
        else if (tok[0][0] == 'v' && tok[0][1] == 0 && ntok >= 5)
        {
            ConvexVolume vol;
            vol.nverts = atoi(tok[1]);
            vol.area = (unsigned char)atoi(tok[2]);
            vol.hmin = (float)atof(tok[3]);
            vol.hmax = (float)atof(tok[4]);
            if (vol.nverts < 0 || vol.nverts > 16)
                continue;
            for (int i = 0; i < vol.nverts; ++i)
            {
                if (!fgets(line, sizeof(line), fp))
                    break;
                float x, z, y; // written as X Z Y (recast y is up)
                if (sscanf(line, "%f %f %f", &x, &z, &y) != 3)
                    break;
                vol.verts[i * 3 + 0] = x;
                vol.verts[i * 3 + 1] = y;
                vol.verts[i * 3 + 2] = z;
            }
            geom.volumes.push_back(vol);
        }
        else if (tok[0][0] == 'c' && tok[0][1] == 0 && ntok >= 11)
        {
            if ((int)geom.omcRadius.size() >= kMaxOffMeshConnections)
                continue; // demo: log + ignore further connections
            float s[3], e[3];
            s[0] = (float)atof(tok[1]); // X
            s[1] = (float)atof(tok[2]); // Z (up)
            s[2] = (float)atof(tok[3]); // Y
            e[0] = (float)atof(tok[4]);
            e[1] = (float)atof(tok[5]);
            e[2] = (float)atof(tok[6]);
            float radius = (float)atof(tok[7]);
            int bidir = atoi(tok[8]);
            int area = atoi(tok[9]);
            int flags = atoi(tok[10]);
            for (int i = 0; i < 3; ++i)
                geom.omcVerts.push_back(s[i]);
            for (int i = 0; i < 3; ++i)
                geom.omcVerts.push_back(e[i]);
            geom.omcRadius.push_back(radius);
            geom.omcBidir.push_back((unsigned char)bidir);
            geom.omcArea.push_back((unsigned char)area);
            geom.omcFlags.push_back((unsigned short)flags);
        }
        // 's' settings lines: recognized by the demo, never written by BuildNav
    }
    fclose(fp);
    return ok;
}

class BuildContext : public rcContext
{
public:
    BuildContext() : rcContext(false) {} // the demo logs errors only (headless)

    void doLog(const rcLogCategory category, const char *msg, const int) override
    {
        if (category == RC_LOG_ERROR)
            fprintf(stderr, "recast_cli: %s\n", msg);
    }
};

// ---------------------------------------------------------------------------
// buildTileMesh - port of Sample_TileMesh::buildTileMesh (upstream; identical
// structure in the tweaked exe, FUN_140016c60).
// ---------------------------------------------------------------------------
unsigned char *buildTileMesh(BuildContext &ctx,
                             const rcConfig &baseCfg,
                             const GeomSet &geom,
                             const rcChunkyTriMesh &chunkyMesh,
                             const int tileX,
                             const int tileY,
                             const float *boundsMin,
                             const float *boundsMax,
                             int &outDataSize)
{
    const int numVerts = (int)geom.verts.size() / 3;
    const int numTris = (int)geom.tris.size() / 3;
    const float *verts = geom.verts.data();

    rcConfig cfg;
    memset(&cfg, 0, sizeof(cfg));
    cfg.cs = baseCfg.cs;
    cfg.ch = baseCfg.ch;
    cfg.walkableSlopeAngle = baseCfg.walkableSlopeAngle;
    cfg.walkableHeight = baseCfg.walkableHeight;
    cfg.walkableClimb = baseCfg.walkableClimb;
    cfg.walkableRadius = baseCfg.walkableRadius;
    cfg.maxEdgeLen = baseCfg.maxEdgeLen;
    cfg.maxSimplificationError = baseCfg.maxSimplificationError;
    cfg.minRegionArea = baseCfg.minRegionArea;
    cfg.mergeRegionArea = baseCfg.mergeRegionArea;
    cfg.maxVertsPerPoly = baseCfg.maxVertsPerPoly;
    cfg.tileSize = baseCfg.tileSize;
    cfg.borderSize = baseCfg.borderSize;
    cfg.width = baseCfg.width;
    cfg.height = baseCfg.height;
    cfg.detailSampleDist = baseCfg.detailSampleDist;
    cfg.detailSampleMaxError = baseCfg.detailSampleMaxError;

    rcVcopy(cfg.bmin, boundsMin);
    rcVcopy(cfg.bmax, boundsMax);
    cfg.bmin[0] -= (float)cfg.borderSize * cfg.cs;
    cfg.bmin[2] -= (float)cfg.borderSize * cfg.cs;
    cfg.bmax[0] += (float)cfg.borderSize * cfg.cs;
    cfg.bmax[2] += (float)cfg.borderSize * cfg.cs;

    rcHeightfield *hf = rcAllocHeightfield();
    if (!hf || !rcCreateHeightfield(&ctx, *hf, cfg.width, cfg.height, cfg.bmin, cfg.bmax, cfg.cs, cfg.ch))
    {
        fprintf(stderr, "recast_cli: buildNavigation: Could not create solid heightfield.\n");
        rcFreeHeightField(hf);
        return nullptr;
    }

    float tileBoundsMin[2] = {cfg.bmin[0], cfg.bmin[2]};
    float tileBoundsMax[2] = {cfg.bmax[0], cfg.bmax[2]};
    int cid[512]; // same 512-chunk cap as the demo (Sample_TileMesh TODO: grow)
    const int ncid = rcGetChunksOverlappingRect(&chunkyMesh, tileBoundsMin, tileBoundsMax, cid, 512);
    if (!ncid)
    {
        rcFreeHeightField(hf);
        return nullptr;
    }

    unsigned char *triareas = new unsigned char[chunkyMesh.maxTrisPerChunk];
    int tileTriCount = 0;
    for (int i = 0; i < ncid; ++i)
    {
        const rcChunkyTriMeshNode &node = chunkyMesh.nodes[cid[i]];
        const int *nodeTris = &chunkyMesh.tris[node.i * 3];
        const int numNodeTris = node.n;
        tileTriCount += numNodeTris;

        memset(triareas, 0, numNodeTris * sizeof(unsigned char));
        rcMarkWalkableTriangles(&ctx, cfg.walkableSlopeAngle, verts, numVerts, nodeTris, numNodeTris, triareas);
        if (!rcRasterizeTriangles(&ctx, verts, numVerts, nodeTris, triareas, numNodeTris, *hf, cfg.walkableClimb))
        {
            delete[] triareas;
            rcFreeHeightField(hf);
            return nullptr;
        }
    }
    delete[] triareas;

    rcFilterLowHangingWalkableObstacles(&ctx, cfg.walkableClimb, *hf);
    rcFilterLedgeSpans(&ctx, cfg.walkableHeight, cfg.walkableClimb, *hf);
    rcFilterWalkableLowHeightSpans(&ctx, cfg.walkableHeight, *hf);

    rcCompactHeightfield *chf = rcAllocCompactHeightfield();
    if (!chf || !rcBuildCompactHeightfield(&ctx, cfg.walkableHeight, cfg.walkableClimb, *hf, *chf))
    {
        fprintf(stderr, "recast_cli: buildNavigation: Could not build compact data.\n");
        rcFreeHeightField(hf);
        rcFreeCompactHeightfield(chf);
        return nullptr;
    }
    rcFreeHeightField(hf);

    if (!rcErodeWalkableArea(&ctx, cfg.walkableRadius, *chf))
    {
        fprintf(stderr, "recast_cli: buildNavigation: Could not erode.\n");
        rcFreeCompactHeightfield(chf);
        return nullptr;
    }

    for (const ConvexVolume &vol : geom.volumes)
        rcMarkConvexPolyArea(&ctx, vol.verts, vol.nverts, vol.hmin, vol.hmax, vol.area, *chf);

    // partitionType == WATERSHED (0)
    if (!rcBuildDistanceField(&ctx, *chf))
    {
        fprintf(stderr, "recast_cli: buildNavigation: Could not build distance field.\n");
        rcFreeCompactHeightfield(chf);
        return nullptr;
    }
    if (!rcBuildRegions(&ctx, *chf, cfg.borderSize, cfg.minRegionArea, cfg.mergeRegionArea))
    {
        fprintf(stderr, "recast_cli: buildNavigation: Could not build watershed regions.\n");
        rcFreeCompactHeightfield(chf);
        return nullptr;
    }

    rcContourSet *cset = rcAllocContourSet();
    if (!cset || !rcBuildContours(&ctx, *chf, cfg.maxSimplificationError, cfg.maxEdgeLen, *cset, 1))
    {
        fprintf(stderr, "recast_cli: buildNavigation: Could not create contours.\n");
        rcFreeCompactHeightfield(chf);
        rcFreeContourSet(cset);
        return nullptr;
    }
    if (cset->nconts == 0)
    {
        rcFreeCompactHeightfield(chf);
        rcFreeContourSet(cset);
        return nullptr;
    }

    rcPolyMesh *pmeshOut = rcAllocPolyMesh();
    if (!pmeshOut || !rcBuildPolyMesh(&ctx, *cset, cfg.maxVertsPerPoly, *pmeshOut))
    {
        fprintf(stderr, "recast_cli: buildNavigation: Could not triangulate contours.\n");
        rcFreeCompactHeightfield(chf);
        rcFreeContourSet(cset);
        rcFreePolyMesh(pmeshOut);
        return nullptr;
    }

    rcPolyMeshDetail *dmesh = rcAllocPolyMeshDetail();
    if (!dmesh || !rcBuildPolyMeshDetail(&ctx, *pmeshOut, *chf, cfg.detailSampleDist, cfg.detailSampleMaxError, *dmesh))
    {
        fprintf(stderr, "recast_cli: buildNavigation: Could not build detail mesh.\n");
        rcFreeCompactHeightfield(chf);
        rcFreeContourSet(cset);
        rcFreePolyMesh(pmeshOut);
        rcFreePolyMeshDetail(dmesh);
        return nullptr;
    }

    rcFreeCompactHeightfield(chf);
    rcFreeContourSet(cset);

    if (cfg.maxVertsPerPoly <= DT_VERTS_PER_POLYGON && pmeshOut->nverts >= 0xffff)
    {
        fprintf(stderr, "recast_cli: Too many vertices per tile %d (max: %d).\n", pmeshOut->nverts, 0xffff);
        rcFreePolyMesh(pmeshOut);
        rcFreePolyMeshDetail(dmesh);
        return nullptr;
    }

    // Update poly flags from areas (stock Sample_TileMesh).
    for (int i = 0; i < pmeshOut->npolys; ++i)
    {
        if (pmeshOut->areas[i] == RC_WALKABLE_AREA)
            pmeshOut->areas[i] = AREA_GROUND;

        if (pmeshOut->areas[i] == AREA_GROUND || pmeshOut->areas[i] == AREA_GRASS || pmeshOut->areas[i] == AREA_ROAD)
            pmeshOut->flags[i] = FLAG_WALK;
        else if (pmeshOut->areas[i] == AREA_WATER)
            pmeshOut->flags[i] = FLAG_SWIM;
        else if (pmeshOut->areas[i] == AREA_DOOR)
            pmeshOut->flags[i] = FLAG_WALK | FLAG_DOOR;
        // AREA_JUMP polygons keep flags 0 (stock behavior).
    }

    dtNavMeshCreateParams params;
    memset(&params, 0, sizeof(params));
    params.verts = pmeshOut->verts;
    params.vertCount = pmeshOut->nverts;
    params.polys = pmeshOut->polys;
    params.polyAreas = pmeshOut->areas;
    params.polyFlags = pmeshOut->flags;
    params.polyCount = pmeshOut->npolys;
    params.nvp = pmeshOut->nvp;
    params.detailMeshes = dmesh->meshes;
    params.detailVerts = dmesh->verts;
    params.detailVertsCount = dmesh->nverts;
    params.detailTris = dmesh->tris;
    params.detailTriCount = dmesh->ntris;
    params.offMeshConVerts = geom.omcVerts.empty() ? nullptr : geom.omcVerts.data();
    params.offMeshConRad = geom.omcRadius.empty() ? nullptr : geom.omcRadius.data();
    params.offMeshConDir = geom.omcBidir.empty() ? nullptr : geom.omcBidir.data();
    params.offMeshConAreas = geom.omcArea.empty() ? nullptr : geom.omcArea.data();
    params.offMeshConFlags = geom.omcFlags.empty() ? nullptr : geom.omcFlags.data();
    params.offMeshConUserID = nullptr;
    params.offMeshConCount = (int)geom.omcRadius.size();
    params.walkableHeight = kAgentHeight;
    params.walkableRadius = kAgentRadius;
    params.walkableClimb = kAgentMaxClimb;
    params.tileX = tileX;
    params.tileY = tileY;
    params.tileLayer = 0;
    rcVcopy(params.bmin, pmeshOut->bmin);
    rcVcopy(params.bmax, pmeshOut->bmax);
    params.cs = cfg.cs;
    params.ch = cfg.ch;
    params.buildBvTree = true;

    unsigned char *navData = nullptr;
    int navDataSize = 0;
    if (!dtCreateNavMeshData(&params, &navData, &navDataSize))
    {
        fprintf(stderr, "recast_cli: Could not build Detour navmesh.\n");
        rcFreePolyMesh(pmeshOut);
        rcFreePolyMeshDetail(dmesh);
        return nullptr;
    }

    rcFreePolyMesh(pmeshOut);
    rcFreePolyMeshDetail(dmesh);
    outDataSize = navDataSize;
    return navData;
}

// ---------------------------------------------------------------------------
// NAVMESHSET writer - matches DetourInterface.cpp LoadNavMesh.
// ---------------------------------------------------------------------------
bool saveNavMeshSet(const char *path, const dtNavMesh *mesh, const float *orig, float tileWorldSize)
{
    FILE *fp = fopen(path, "wb");
    if (!fp)
    {
        fprintf(stderr, "recast_cli: cannot write '%s'\n", path);
        return false;
    }

    struct Header
    {
        int magic;
        int version;
        int numTiles;
        float orig[3];
        float cs;
        float ch;
        unsigned int maxTiles;
        unsigned int maxPolys;
    };

    Header hdr;
    hdr.magic = 0x4d534554; // 'MSET'
    hdr.version = 1;
    int numTiles = 0;
    for (int i = 0; i < mesh->getMaxTiles(); ++i)
    {
        const dtMeshTile *tile = mesh->getTile(i);
        if (tile && tile->header)
            numTiles++;
    }
    hdr.numTiles = numTiles;
    hdr.orig[0] = orig[0];
    hdr.orig[1] = orig[1];
    hdr.orig[2] = orig[2];
    hdr.cs = tileWorldSize;
    hdr.ch = tileWorldSize;
    hdr.maxTiles = kMaxTiles;
    hdr.maxPolys = kMaxPolys;
    fwrite(&hdr, sizeof(hdr), 1, fp);

    for (int i = 0; i < mesh->getMaxTiles(); ++i)
    {
        const dtMeshTile *tile = mesh->getTile(i);
        if (!tile || !tile->header)
            continue;

        struct TileHeader
        {
            dtTileRef ref;
            int size;
            int pad; // struct alignment padding, LoadNavMesh reads 16 bytes/entry
        };
        TileHeader th;
        th.ref = mesh->getTileRef(tile);
        th.size = tile->dataSize;
        th.pad = 0;
        fwrite(&th, sizeof(th), 1, fp);
        fwrite(tile->data, tile->dataSize, 1, fp);
    }

    fclose(fp);
    return true;
}

} // namespace

int main(int argc, char **argv)
{
    if (argc < 3)
    {
        fprintf(stderr, "usage: recast_cli <in.gset> <out.nav>\n");
        return 2;
    }

    GeomSet geom;
    if (!loadGset(argv[1], geom))
        return 1;

    const int numVerts = (int)geom.verts.size() / 3;
    const int numTris = (int)geom.tris.size() / 3;
    if (numTris == 0)
    {
        fprintf(stderr, "recast_cli: no triangles\n");
        return 1;
    }

    rcChunkyTriMesh cmesh;
    if (!rcCreateChunkyTriMesh(geom.verts.data(), geom.tris.data(), numTris, kTrisPerChunk, &cmesh))
    {
        fprintf(stderr, "recast_cli: could not build chunky tri mesh\n");
        return 1;
    }

    // Base rcConfig (matches the decompiled cfg fill exactly).
    rcConfig cfg;
    memset(&cfg, 0, sizeof(cfg));
    cfg.cs = kCellSize;
    cfg.ch = kCellHeight;
    cfg.walkableSlopeAngle = (int)kAgentMaxSlope;
    cfg.walkableHeight = (int)ceilf(kAgentHeight / kCellHeight);
    cfg.walkableClimb = (int)floorf(kAgentMaxClimb / kCellHeight);
    cfg.walkableRadius = (int)ceilf(kAgentRadius / kCellSize);
    cfg.maxEdgeLen = (int)(kEdgeMaxLen / kCellSize);
    cfg.maxSimplificationError = kEdgeMaxError;
    cfg.minRegionArea = (int)(kRegionMinSize * kRegionMinSize);
    cfg.mergeRegionArea = (int)(kRegionMergeSize * kRegionMergeSize);
    cfg.maxVertsPerPoly = (int)kVertsPerPoly;
    cfg.tileSize = kTileSize;
    cfg.borderSize = cfg.walkableRadius + 3;
    cfg.width = cfg.tileSize + cfg.borderSize * 2;
    cfg.height = cfg.tileSize + cfg.borderSize * 2;
    cfg.detailSampleDist = kDetailSampleDist < 0.9f ? 0 : kCellSize * kDetailSampleDist;
    cfg.detailSampleMaxError = kCellHeight * kDetailSampleMaxError;

    int gridWidth = 0, gridHeight = 0;
    rcCalcGridSize(geom.bmin, geom.bmax, cfg.cs, &gridWidth, &gridHeight);
    const int tileWidth = (gridWidth + cfg.tileSize - 1) / cfg.tileSize;
    const int tileHeight = (gridHeight + cfg.tileSize - 1) / cfg.tileSize;
    const float tileCellSize = (float)cfg.tileSize * cfg.cs;

    fprintf(stderr, "recast_cli: %d verts %d tris, grid %dx%d, tiles %dx%d\n",
            numVerts, numTris, gridWidth, gridHeight, tileWidth, tileHeight);

    dtNavMeshParams nparams;
    memset(&nparams, 0, sizeof(nparams));
    nparams.orig[0] = geom.bmin[0];
    nparams.orig[1] = geom.bmin[1];
    nparams.orig[2] = geom.bmin[2];
    nparams.tileWidth = tileCellSize;  // dtNavMeshParams.cs/ch in the shipped files
    nparams.tileHeight = tileCellSize;
    nparams.maxTiles = kMaxTiles;
    nparams.maxPolys = kMaxPolys;

    dtNavMesh *navMesh = dtAllocNavMesh();
    if (!navMesh || dtStatusFailed(navMesh->init(&nparams)))
    {
        fprintf(stderr, "recast_cli: Could not init Detour navmesh.\n");
        return 1;
    }

    BuildContext ctx;
    int builtTiles = 0, totalPolys = 0, totalVerts = 0;

    // ponytail: tile-level OpenMP. Per-tile builds are independent and
    // deterministic (same output as serial); only addTile stays serial.
    // addTile order kept (y outer, x inner) to match the serial build.
    struct TileResult
    {
        unsigned char *data;
        int size;
    };
    std::vector<TileResult> results((size_t)tileWidth * tileHeight);

    int maxY = tileHeight, maxX = tileWidth;
#pragma omp parallel for schedule(dynamic, 1) collapse(2)
    for (int y = 0; y < maxY; ++y)
    {
        for (int x = 0; x < maxX; ++x)
        {
            TileResult &res = results[(size_t)y * tileWidth + x];
            res.data = nullptr;
            res.size = 0;

            float tileBmin[3], tileBmax[3];
            tileBmin[0] = geom.bmin[0] + (float)x * tileCellSize;
            tileBmin[1] = geom.bmin[1];
            tileBmin[2] = geom.bmin[2] + (float)y * tileCellSize;
            tileBmax[0] = geom.bmin[0] + (float)(x + 1) * tileCellSize;
            tileBmax[1] = geom.bmax[1];
            tileBmax[2] = geom.bmin[2] + (float)(y + 1) * tileCellSize;

            int dataSize = 0;
            unsigned char *data = buildTileMesh(ctx, cfg, geom, cmesh, x, y, tileBmin, tileBmax, dataSize);
            res.data = data;
            res.size = data ? dataSize : 0;
        }
    }

    for (const TileResult &res : results)
    {
        if (!res.data)
            continue;
        dtStatus status = navMesh->addTile(res.data, res.size, DT_TILE_FREE_DATA, 0, nullptr);
        if (dtStatusFailed(status))
        {
            dtFree(res.data);
            continue;
        }
        ++builtTiles;
    }

    // Totals for the log.
    const dtNavMesh *constMesh = navMesh;
    for (int i = 0; i < constMesh->getMaxTiles(); ++i)
    {
        const dtMeshTile *tile = constMesh->getTile(i);
        if (tile && tile->header)
        {
            totalPolys += tile->header->polyCount;
            totalVerts += tile->header->vertCount;
        }
    }
    fprintf(stderr, "recast_cli: built %d tiles, %d polys, %d verts, %d offmesh\n",
            builtTiles, totalPolys, totalVerts, (int)geom.omcRadius.size());

    if (!saveNavMeshSet(argv[2], constMesh, geom.bmin, tileCellSize))
        return 1;

    dtFreeNavMesh(navMesh);
    return 0;
}
