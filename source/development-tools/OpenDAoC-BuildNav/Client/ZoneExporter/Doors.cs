using System.Text.RegularExpressions;
using System.Drawing;
using MNL;
using OpenTK;

namespace CEM.Client.ZoneExporter
{
  /// <summary>
  /// Doors
  /// </summary>
  partial class Zone2Obj
  {
    private static readonly Regex[] DoorRegex = [new("^door([0-9])+")];

    /// <summary>
    /// Door leaves that are not numbered server doors: "door L01", "doorR", "door_01:31",
    /// "door_01_collidee", "door", "doors", "doorPanel"... Trim, arches, doorways and handles
    /// are not leaves. In a house whose doors the server does not control, players walk
    /// through these, but they used to be built as solid walls, sealing e.g. every Hibernian
    /// Celtic hut (merchants and trainers inside unreachable, 2026-10-06 audit).
    /// </summary>
    private static readonly Regex[] DoorLeafRegex = [new(@"^doors?(\s*[lr]?\s*\d*|_\d+(_collidee)?|_coll(idee)?|panel|\s+\d+)?(:\d+)?$")];

    private void ExtractDoor(NiFile model, Matrix4 worldMatrix, int fixtureid)
    {
      // take everything that is under a doorX node and use it for the hull...
      if (model == null) return;

      var doorVertices = new Dictionary<string, List<Vector3>>();
      var leafVertices = new List<List<Vector3>>();

      // find all trimeshs and tristrips
      foreach (var obj in model.ObjectsByRef.Values)
      {
        var avNode = obj as NiAVObject;
        if (avNode == null) continue;

        if (!IsMatched(avNode, new[] { "visible", "collidee", "collide" }))
          continue;

        var doorName = FindMatchRegex(avNode, DoorRegex);
        bool leaf = doorName == string.Empty && FindMatchRegex(avNode, DoorLeafRegex) != string.Empty;

        if (doorName == string.Empty && !leaf) continue;

        Vector3[] vertices = null;
        Triangle[] triangles = null;

        TryExtractTriShape(obj, worldMatrix, false, false, ref vertices, ref triangles);

        if (vertices == null)
        {
          TryExtractTriStrips(obj, worldMatrix, false, false, ref vertices, ref triangles);
        }

        if (vertices == null)
          continue;

        if (leaf)
        {
          leafVertices.Add(vertices.ToList());
          continue;
        }

        if (!doorVertices.ContainsKey(doorName))
        {
          doorVertices.Add(doorName, new List<Vector3>());
        }
        doorVertices[doorName].AddRange(vertices);
      }


      // A door leaf belongs to the nearest numbered door of the same model, so the server's
      // open/closed state still gates it (keep gates stay closed to bots when closed). Leaves
      // of a model without numbered doors are simply walkable.
      foreach (var leafSet in leafVertices)
      {
        if (doorVertices.Count == 0) break;
        Vector3 centre = leafSet.Aggregate(Vector3.Zero, (sum, v) => sum + v) / leafSet.Count;
        string nearest = doorVertices.Keys.OrderBy(key =>
          (doorVertices[key].Aggregate(Vector3.Zero, (sum, v) => sum + v) / doorVertices[key].Count - centre).LengthSquared).First();
        if ((doorVertices[nearest].Aggregate(Vector3.Zero, (sum, v) => sum + v) / doorVertices[nearest].Count - centre).LengthSquared < 400f * 400f)
          doorVertices[nearest].AddRange(leafSet);
      }

      foreach (var key in doorVertices.Keys)
      {
        float hullMinZ = float.MaxValue;
        float hullMaxZ = float.MinValue;

        var verts = doorVertices[key].ToArray();

        var pts = new List<PointF>();
        foreach (Vector3 vert in verts)
        {
          hullMinZ = Math.Min(vert.Z, hullMinZ);
          hullMaxZ = Math.Max(vert.Z, hullMaxZ);
          pts.Add(new PointF(vert.X, vert.Y));
        }

        MinAreaRect box = ComputeMinAreaRect(pts.ToArray());

        float maxSize = box.Size.Width;
        maxSize = Math.Max(maxSize, box.Size.Height);
        maxSize = Math.Max(maxSize, hullMaxZ - hullMinZ);

        // There are some weird door ids in e.g. Jordheim (z120): "door01:0" e.g. -- how do they translate to IDs?
        var doorID = Regex.Match(key.Replace(":", ""), "([0-9]+)").Groups[1].Value;
        var heading = ((box.Angle + (box.Size.Width < box.Size.Height ? 0.0 : 90.0 + 90.0)) * DEGREES_TO_HEADING) % 0x1000;
        if (heading < 0)
        {
          heading += 0x1000;
        }
        DoorWriter.WriteDoor(Zone.ID * 1000000 + fixtureid * 100 + int.Parse(doorID), model.FileName, (int)box.Center.X, (int)box.Center.Y, (int)hullMinZ, (int)heading, (int)maxSize);


        // Make sure we have a min of 20f for doors on width/height
        box.Size.Width = Math.Max(20f, box.Size.Width);
        box.Size.Height = Math.Max(20f, box.Size.Height);

        // Make sure the door touches the ground...
        hullMinZ -= 16.0f;

        var boxVertices = box.GetVertices();
        GeomSetWriter.WriteConvexVolume(boxVertices.Length, hullMinZ, hullMaxZ, CEM.GeomSetWriter.eAreas.Door);

        foreach (var vert in boxVertices)
          GeomSetWriter.WriteConvexVolumeVertex(new Vector3(vert.X, vert.Y, hullMinZ)); // debug
      }
    }

    /// <summary>
    /// Local min-area rectangle: monotone-chain convex hull + rotating calipers.
    /// ponytail: replaces Emgu CvInvoke.MinAreaRect (Emgu has no linux-arm64 natives).
    /// Center/Size/GetVertices() describe the same enclosing rectangle; Angle uses the
    /// OpenCV >= 3.2 convention (degrees in (-90, 0], width axis along Angle) so the
    /// heading arithmetic below keeps its shape. Only the gset door volume matters
    /// downstream (.doors files are not read by the server); geometry is exact, the
    /// informational heading may differ from Emgu by a 90-degree/w-h-swap equivalent.
    /// </summary>
    private sealed class MinAreaRect
    {
      public PointF Center;
      public SizeF Size;
      public float Angle;

      public PointF[] GetVertices()
      {
        double a = Angle * Math.PI / 180.0;
        float c = (float) Math.Cos(a);
        float s = (float) Math.Sin(a);
        float hw = Size.Width / 2f;
        float hh = Size.Height / 2f;
        float[,] local = { {-hw, -hh}, {hw, -hh}, {hw, hh}, {-hw, hh} };
        var pts = new PointF[4];
        for (int i = 0; i < 4; i++)
        {
          float lx = local[i, 0];
          float ly = local[i, 1];
          pts[i] = new PointF(Center.X + lx * c - ly * s, Center.Y + lx * s + ly * c);
        }

        return pts;
      }
    }

    private static MinAreaRect ComputeMinAreaRect(PointF[] pts)
    {
      var rect = new MinAreaRect();
      if (pts.Length == 0)
        return rect;
      if (pts.Length <= 2)
      {
        float minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X);
        float minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
        rect.Center = new PointF((minX + maxX) / 2f, (minY + maxY) / 2f);
        rect.Size = new SizeF(maxX - minX, maxY - minY);
        rect.Angle = 0f;
        return rect;
      }

      // Monotone chain convex hull (points sorted lexicographically).
      var sorted = pts.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToArray();
      if (sorted.Length < 3)
        return ComputeMinAreaRect(sorted);

      var hull = new List<PointF>();
      foreach (var p in sorted.Concat(sorted.Reverse().Skip(1)))
      {
        while (hull.Count >= 2 &&
               (double) (hull[hull.Count - 1].X - hull[hull.Count - 2].X) * (p.Y - hull[hull.Count - 2].Y) -
               (double) (hull[hull.Count - 1].Y - hull[hull.Count - 2].Y) * (p.X - hull[hull.Count - 2].X) <= 0)
          hull.RemoveAt(hull.Count - 1);
        hull.Add(p);
      }

      hull.RemoveAt(hull.Count - 1);

      // Rotating calipers: minimal-area enclosing rectangle over hull edge orientations.
      double bestArea = double.MaxValue;
      float bestTheta = 0f;
      float bestW = 0f, bestH = 0f;
      PointF bestCenter = PointF.Empty;
      int n = hull.Count;
      for (int i = 0; i < n; i++)
      {
        float ax = hull[i].X, ay = hull[i].Y;
        float bx = hull[(i + 1) % n].X, by = hull[(i + 1) % n].Y;
        float theta = (float) (Math.Atan2(by - ay, bx - ax));
        float c = (float) Math.Cos(-theta);
        float s = (float) Math.Sin(-theta);
        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        foreach (var p in hull)
        {
          float rx = p.X * c - p.Y * s;
          float ry = p.X * s + p.Y * c;
          if (rx < minX) minX = rx;
          if (rx > maxX) maxX = rx;
          if (ry < minY) minY = ry;
          if (ry > maxY) maxY = ry;
        }

        double area = (double) (maxX - minX) * (maxY - minY);
        if (area < bestArea)
        {
          bestArea = area;
          bestTheta = theta;
          bestW = maxX - minX;
          bestH = maxY - minY;
          float cx = (minX + maxX) / 2f;
          float cy = (minY + maxY) / 2f;
          bestCenter = new PointF(cx * (float) Math.Cos(theta) - cy * (float) Math.Sin(theta),
                                  cx * (float) Math.Sin(theta) + cy * (float) Math.Cos(theta));
        }
      }

      // Normalize to OpenCV >=3.2 convention: angle in (-90, 0], width axis along angle.
      float deg = (float) (bestTheta * 180.0 / Math.PI);
      while (deg > 90f) deg -= 180f;
      while (deg <= -90f) deg += 180f;
      if (deg > 0f)
      {
        deg -= 90f;
        (bestW, bestH) = (bestH, bestW);
      }

      rect.Center = bestCenter;
      rect.Size = new SizeF(bestW, bestH);
      rect.Angle = deg;
      return rect;
    }

  }
}
