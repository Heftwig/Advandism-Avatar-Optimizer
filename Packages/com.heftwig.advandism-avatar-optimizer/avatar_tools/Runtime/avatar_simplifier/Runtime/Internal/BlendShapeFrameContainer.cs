#region License
/*
MIT License

Copyright(c) 2017-2020 Mattias Edlund

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/
#endregion

using System.Runtime.CompilerServices;
using UnityEngine;

namespace UnityMeshSimplifier.Internal
{
    internal class BlendShapeFrameContainer
    {
        private readonly float frameWeight;
        private readonly bool hasDeltaNormals;
        private readonly bool hasDeltaTangents;
        private readonly ResizableArray<Vector3> deltaVertices;
        private readonly ResizableArray<Vector3> deltaNormals;
        private readonly ResizableArray<Vector3> deltaTangents;

        public BlendShapeFrameContainer(BlendShapeFrame frame)
        {
            frameWeight = frame.FrameWeight;
            hasDeltaNormals = frame.DeltaNormals != null;
            hasDeltaTangents = frame.DeltaTangents != null;
            deltaVertices = new ResizableArray<Vector3>(frame.DeltaVertices);
            deltaNormals = new ResizableArray<Vector3>(frame.DeltaNormals ?? new Vector3[frame.DeltaVertices.Length]);
            deltaTangents = new ResizableArray<Vector3>(frame.DeltaTangents ?? new Vector3[frame.DeltaVertices.Length]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void MoveVertexElement(int dst, int src)
        {
            deltaVertices[dst] = deltaVertices[src];
            deltaNormals[dst] = deltaNormals[src];
            deltaTangents[dst] = deltaTangents[src];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void InterpolateVertexAttributes(int dst, int i0, int i1, int i2, ref Vector3 barycentricCoord)
        {
            deltaVertices[dst] = (deltaVertices[i0] * barycentricCoord.x) + (deltaVertices[i1] * barycentricCoord.y) + (deltaVertices[i2] * barycentricCoord.z);
            // Blend shape normals and tangents are delta vectors, not unit directions.
            // Normalizing them destroys the authored delta magnitude.
            deltaNormals[dst] = (deltaNormals[i0] * barycentricCoord.x) + (deltaNormals[i1] * barycentricCoord.y) + (deltaNormals[i2] * barycentricCoord.z);
            deltaTangents[dst] = (deltaTangents[i0] * barycentricCoord.x) + (deltaTangents[i1] * barycentricCoord.y) + (deltaTangents[i2] * barycentricCoord.z);
        }

        public void Resize(int length, bool trimExess = false)
        {
            deltaVertices.Resize(length, trimExess);
            deltaNormals.Resize(length, trimExess);
            deltaTangents.Resize(length, trimExess);
        }

        public BlendShapeFrame ToBlendShapeFrame()
        {
            var resultVertices = deltaVertices.ToArray();
            var resultNormals = hasDeltaNormals ? deltaNormals.ToArray() : null;
            var resultTangents = hasDeltaTangents ? deltaTangents.ToArray() : null;
            return new BlendShapeFrame(frameWeight, resultVertices, resultNormals, resultTangents);
        }
    }
}
