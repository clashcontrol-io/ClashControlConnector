using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using ClashControlConnector.Protocol;

namespace ClashControlConnector.Core
{
    /// <summary>
    /// Extracts triangulated mesh data from Revit elements.
    /// Converts from Revit feet/Z-up to ClashControl meters/Y-up.
    /// </summary>
    public static class GeometryExporter
    {
        public static ElementGeometry ExtractGeometry(Element element)
        {
            var positions = new List<float>();
            var indices = new List<uint>();
            var normals = new List<float>();

            var options = new Options
            {
                ComputeReferences = true,
                DetailLevel = ViewDetailLevel.Fine
            };

            var geomElement = element.get_Geometry(options);
            if (geomElement == null) return null;

            uint vertexOffset = 0;
            ProcessGeometry(geomElement, Transform.Identity, positions, indices, normals, ref vertexOffset);

            if (positions.Count == 0) return null;

            return new ElementGeometry
            {
                Positions = Convert.ToBase64String(FloatListToBytes(positions)),
                Indices = Convert.ToBase64String(UIntListToBytes(indices)),
                Normals = Convert.ToBase64String(FloatListToBytes(normals))
            };
        }

        private static void ProcessGeometry(GeometryElement geomElement, Transform transform,
            List<float> positions, List<uint> indices, List<float> normals, ref uint vertexOffset)
        {
            foreach (var geomObj in geomElement)
            {
                switch (geomObj)
                {
                    case Solid solid:
                        if (solid.Volume > 0)
                            ProcessSolid(solid, transform, positions, indices, normals, ref vertexOffset);
                        break;

                    case GeometryInstance instance:
                        // GetInstanceGeometry() already applies the instance transform
                        var instanceGeom = instance.GetInstanceGeometry();
                        if (instanceGeom != null)
                            ProcessGeometry(instanceGeom, Transform.Identity, positions, indices, normals, ref vertexOffset);
                        break;
                }
            }
        }

        private static void ProcessSolid(Solid solid, Transform transform,
            List<float> positions, List<uint> indices, List<float> normals, ref uint vertexOffset)
        {
            foreach (Face face in solid.Faces)
            {
                Mesh mesh = face.Triangulate();
                if (mesh == null) continue;

                int meshVertCount = mesh.Vertices.Count;

                // Compute face normal
                XYZ faceNormal = face.ComputeNormal(new UV(0.5, 0.5));
                XYZ transformedNormal = transform.IsIdentity ? faceNormal : transform.OfVector(faceNormal);

                // Normals: Revit Z-up → Y-up (no scale — unit vectors)
                float nx = (float)transformedNormal.X;
                float ny = (float)transformedNormal.Z;
                float nz = (float)(-transformedNormal.Y);

                // Add vertices
                for (int i = 0; i < meshVertCount; i++)
                {
                    XYZ pt = mesh.Vertices[i];
                    XYZ transformed = transform.IsIdentity ? pt : transform.OfPoint(pt);

                    // Convert: feet Z-up → meters Y-up
                    positions.Add((float)(transformed.X * 0.3048));
                    positions.Add((float)(transformed.Z * 0.3048));
                    positions.Add((float)(-transformed.Y * 0.3048));

                    // Per-vertex normals (face normal for all vertices of this face)
                    normals.Add(nx);
                    normals.Add(ny);
                    normals.Add(nz);
                }

                // Add triangle indices
                for (int i = 0; i < mesh.NumTriangles; i++)
                {
                    MeshTriangle tri = mesh.get_Triangle(i);
                    indices.Add(vertexOffset + (uint)tri.get_Index(0));
                    indices.Add(vertexOffset + (uint)tri.get_Index(1));
                    indices.Add(vertexOffset + (uint)tri.get_Index(2));
                }

                vertexOffset += (uint)meshVertCount;
            }
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
