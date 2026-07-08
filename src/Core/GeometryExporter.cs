using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using ClashControlConnector.Protocol;

namespace ClashControlConnector.Core
{
    /// <summary>
    /// Extracts triangulated mesh data from Revit elements.
    /// Converts from Revit feet/Z-up to ClashControl meters/Y-up.
    ///
    /// Faces are grouped by material so multi-material elements (e.g. a window's
    /// frame + glass) can be drawn with per-section materials — that's what lets
    /// glass render transparent without the frame going see-through too. Single
    /// material elements emit no groups and keep the flat per-element color.
    /// </summary>
    public static class GeometryExporter
    {
        /// <param name="baseTransform">
        /// Placement applied to all geometry before the feet→meters / Z-up→Y-up
        /// conversion. For host-model elements this is null (identity, geometry is
        /// already in model coords). For LINKED-model elements it must be the
        /// RevitLinkInstance total transform — link geometry is read in the linked
        /// document's own coordinate system, so without this it lands offset/rotated
        /// ("flying") in ClashControl.
        /// </param>
        public static ElementGeometry ExtractGeometry(Element element, Transform baseTransform = null)
        {
            var options = new Options
            {
                ComputeReferences = false,
                DetailLevel = ConnectorSettings.DetailLevel
            };

            var geomElement = element.get_Geometry(options);
            if (geomElement == null) return null;

            var doc = element.Document;
            var fallback = GetFallbackColor(element, doc);

            // One bucket per material. `order` preserves first-seen order so output is
            // deterministic; `colorCache` avoids re-resolving the same Material element.
            var buckets = new Dictionary<long, MaterialBucket>();
            var order = new List<MaterialBucket>();
            var colorCache = new Dictionary<long, float[]>();

            ProcessGeometry(geomElement, baseTransform ?? Transform.Identity,
                doc, fallback, buckets, order, colorCache);

            if (order.Count == 0 || order.All(b => b.Positions.Count == 0))
                return null;

            // Opaque groups first, transparent last — helps the renderer depth-sort
            // (and keeps glass from occluding what's behind it).
            var ordered = order.Where(b => !b.Transparent)
                .Concat(order.Where(b => b.Transparent))
                .ToList();

            var positions = new List<float>();
            var normals = new List<float>();
            var indices = new List<uint>();
            var groups = new List<GeometryGroup>();

            uint baseVertex = 0;
            foreach (var b in ordered)
            {
                if (b.Indices.Count == 0) continue;

                int startIndex = indices.Count;
                positions.AddRange(b.Positions);
                normals.AddRange(b.Normals);
                foreach (var idx in b.Indices) indices.Add(baseVertex + idx);

                groups.Add(new GeometryGroup
                {
                    Start = startIndex,
                    Count = b.Indices.Count,
                    Color = b.Color
                });
                baseVertex += b.VertexCount;
            }

            if (positions.Count == 0) return null;

            var geometry = new ElementGeometry
            {
                Positions = Convert.ToBase64String(FloatListToBytes(positions)),
                Indices = Convert.ToBase64String(UIntListToBytes(indices)),
                Normals = Convert.ToBase64String(FloatListToBytes(normals))
            };

            // Only ship groups when there's genuinely more than one material — a single
            // material element renders fine from the flat per-element color, and the
            // browser keeps its existing fast path for those.
            if (groups.Count > 1)
                geometry.Groups = groups;
            else if (groups.Count == 1)
                // Single-material element: surface the resolved face color as the flat
                // color so the browser renders — and ContentHasher hashes — the actual
                // material color. Callers only fill Color when the exporter left it
                // null, so a material color change now invalidates the content hash.
                geometry.Color = groups[0].Color;

            return geometry;
        }

        private static void ProcessGeometry(GeometryElement geomElement, Transform transform,
            Document doc, float[] fallback,
            Dictionary<long, MaterialBucket> buckets, List<MaterialBucket> order,
            Dictionary<long, float[]> colorCache)
        {
            foreach (var geomObj in geomElement)
            {
                switch (geomObj)
                {
                    case Solid solid:
                        if (solid.Volume > 0)
                            ProcessSolid(solid, transform, doc, fallback, buckets, order, colorCache);
                        break;

                    case GeometryInstance instance:
                        // GetInstanceGeometry() already applies the instance transform
                        // (within this document), but we must still carry the incoming
                        // `transform` — the link placement for linked models — or linked
                        // instanced geometry would ignore the link offset. For the host
                        // model `transform` is identity, so behaviour is unchanged.
                        var instanceGeom = instance.GetInstanceGeometry();
                        if (instanceGeom != null)
                            ProcessGeometry(instanceGeom, transform, doc, fallback, buckets, order, colorCache);
                        break;
                }
            }
        }

        private static void ProcessSolid(Solid solid, Transform transform,
            Document doc, float[] fallback,
            Dictionary<long, MaterialBucket> buckets, List<MaterialBucket> order,
            Dictionary<long, float[]> colorCache)
        {
            foreach (Face face in solid.Faces)
            {
                Mesh mesh = face.Triangulate();
                if (mesh == null) continue;

                ResolveFaceMaterial(face, doc, fallback, colorCache, out long key, out float[] color);
                var bucket = GetBucket(buckets, order, key, color);

                int meshVertCount = mesh.Vertices.Count;

                // Compute face normal
                XYZ faceNormal = face.ComputeNormal(new UV(0.5, 0.5));
                XYZ transformedNormal = faceNormal;
                if (!transform.IsIdentity)
                {
                    // OfVector applies rotation AND any scale in the transform, so
                    // re-normalize (guarding degenerate zero-length results) — a
                    // scaled link placement would otherwise break shading.
                    transformedNormal = transform.OfVector(faceNormal);
                    if (!transformedNormal.IsZeroLength())
                        transformedNormal = transformedNormal.Normalize();
                }

                // Normals: Revit Z-up → Y-up (no scale — unit vectors)
                float nx = (float)transformedNormal.X;
                float ny = (float)transformedNormal.Z;
                float nz = (float)(-transformedNormal.Y);

                // Add vertices to this material's bucket
                for (int i = 0; i < meshVertCount; i++)
                {
                    XYZ pt = mesh.Vertices[i];
                    XYZ transformed = transform.IsIdentity ? pt : transform.OfPoint(pt);

                    // Convert: feet Z-up → meters Y-up
                    bucket.Positions.Add((float)(transformed.X * 0.3048));
                    bucket.Positions.Add((float)(transformed.Z * 0.3048));
                    bucket.Positions.Add((float)(-transformed.Y * 0.3048));

                    // Per-vertex normals (face normal for all vertices of this face)
                    bucket.Normals.Add(nx);
                    bucket.Normals.Add(ny);
                    bucket.Normals.Add(nz);
                }

                // Triangle indices are local to the bucket; offset by vertices already
                // in this bucket. They get rebased to the merged buffer at assembly time.
                for (int i = 0; i < mesh.NumTriangles; i++)
                {
                    MeshTriangle tri = mesh.get_Triangle(i);
                    bucket.Indices.Add(bucket.VertexCount + (uint)tri.get_Index(0));
                    bucket.Indices.Add(bucket.VertexCount + (uint)tri.get_Index(1));
                    bucket.Indices.Add(bucket.VertexCount + (uint)tri.get_Index(2));
                }

                bucket.VertexCount += (uint)meshVertCount;
            }
        }

        private static MaterialBucket GetBucket(Dictionary<long, MaterialBucket> buckets,
            List<MaterialBucket> order, long key, float[] color)
        {
            if (!buckets.TryGetValue(key, out var bucket))
            {
                bucket = new MaterialBucket
                {
                    Color = color,
                    Transparent = color[3] < 0.999f
                };
                buckets[key] = bucket;
                order.Add(bucket);
            }
            return bucket;
        }

        /// <summary>
        /// Resolve a face's material to a draw color + a grouping key. Faces with no
        /// (or an unusable) material collapse to a single fallback group (key -1) so we
        /// don't fragment the mesh into one group per material-less face.
        /// </summary>
        private static void ResolveFaceMaterial(Face face, Document doc, float[] fallback,
            Dictionary<long, float[]> colorCache, out long key, out float[] color)
        {
            var matId = face.MaterialElementId;
            if (matId != null && matId != ElementId.InvalidElementId)
            {
                key = matId.Value;
                if (colorCache.TryGetValue(key, out color))
                    return;

                var mat = doc.GetElement(matId) as Material;
                if (mat != null && mat.Color != null && mat.Color.IsValid)
                {
                    color = new float[]
                    {
                        mat.Color.Red / 255f,
                        mat.Color.Green / 255f,
                        mat.Color.Blue / 255f,
                        1.0f - (mat.Transparency / 100f)
                    };
                    colorCache[key] = color;
                    return;
                }

                // Material exists but has no usable color — treat as fallback, and merge
                // with other fallback faces so we don't emit a near-duplicate group.
                key = -1;
                color = fallback;
                return;
            }

            key = -1;
            color = fallback;
        }

        /// <summary>
        /// Element-level fallback color: first material with a valid color, else neutral
        /// grey. Used for faces that carry no material of their own.
        /// </summary>
        private static float[] GetFallbackColor(Element element, Document doc)
        {
            try
            {
                foreach (var matId in element.GetMaterialIds(false))
                {
                    if (doc.GetElement(matId) is Material mat && mat.Color != null && mat.Color.IsValid)
                    {
                        return new float[]
                        {
                            mat.Color.Red / 255f,
                            mat.Color.Green / 255f,
                            mat.Color.Blue / 255f,
                            1.0f - (mat.Transparency / 100f)
                        };
                    }
                }
            }
            catch { /* material query unavailable — fall through to grey */ }

            return new float[] { 0.65f, 0.65f, 0.65f, 1.0f };
        }

        /// <summary>Per-material accumulation buffer, merged into one mesh at the end.</summary>
        private class MaterialBucket
        {
            public float[] Color;
            public bool Transparent;
            public readonly List<float> Positions = new List<float>();
            public readonly List<float> Normals = new List<float>();
            public readonly List<uint> Indices = new List<uint>();
            public uint VertexCount;
        }

        private static byte[] FloatListToBytes(List<float> list)
        {
            var bytes = new byte[list.Count * 4];
            Buffer.BlockCopy(list.ToArray(), 0, bytes, 0, bytes.Length);
            return bytes;
        }

        private static byte[] UIntListToBytes(List<uint> list)
        {
            var bytes = new byte[list.Count * 4];
            Buffer.BlockCopy(list.ToArray(), 0, bytes, 0, bytes.Length);
            return bytes;
        }
    }
}
