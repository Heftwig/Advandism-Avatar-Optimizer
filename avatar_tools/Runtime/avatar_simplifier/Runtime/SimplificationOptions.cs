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

using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace UnityMeshSimplifier
{
    /// <summary>
    /// Controls where a newly collapsed vertex may be placed.
    /// </summary>
    public enum VertexPlacementMode
    {
        /// <summary>Use the unconstrained quadric-error optimum. This is the legacy behavior.</summary>
        Optimal = 0,
        /// <summary>Clamp every collapse position to the source edge and interpolate attributes along that edge.</summary>
        EdgeInterpolated = 1,
        /// <summary>Project collapse positions onto the surviving local one-ring surface, with edge interpolation as a fallback.</summary>
        SurfaceProjected = 2,
        /// <summary>Use edge interpolation for borders/features and local-surface projection for smooth interior edges.</summary>
        AvatarHybrid = 3,
        /// <summary>Project collapses onto the immutable original surface and original boundary curves, then enforce explicit deviation limits.</summary>
        ReferenceAccurate = 4
    }

    /// <summary>
    /// Options for mesh simplification.
    /// </summary>
    [Serializable]
    [StructLayout(LayoutKind.Auto)]
    public struct SimplificationOptions
    {
        /// <summary>
        /// The default simplification options.
        /// </summary>
        public static readonly SimplificationOptions Default = new SimplificationOptions
        {
            PreserveBorderEdges = false,
            PreserveUVSeamEdges = true,
            PreserveUVFoldoverEdges = true,
            PreserveSubMeshEdges = true,
            PreserveBoneWeightSeams = true,
            PreserveSurfaceCurvature = false,
            PreserveSurfaceEnvelope = false,
            PreserveBilateralSymmetry = false,
            VertexPlacement = VertexPlacementMode.Optimal,
            FeatureAngleDegrees = 45f,
            MaxTriangleNormalDeviationDegrees = 78f,
            MaxSurfaceDeviation = 0.0,
            MaxBoundaryDeviation = 0.0,
            EnableSmartLink = true,
            VertexLinkDistance = double.Epsilon,
            MaxIterationCount = 100,
            Agressiveness = 7.0,
            ManualUVComponentCount = false,
            UVComponentCount = 2,
            MaxBoneWeightsPerVertex = 0,
            BoneWeightThreshold = 0.00001f
        };

        /// <summary>
        /// Conservative settings intended for skinned avatar meshes.
        /// </summary>
        public static readonly SimplificationOptions Avatar = new SimplificationOptions
        {
            PreserveBorderEdges = false,
            PreserveUVSeamEdges = true,
            PreserveUVFoldoverEdges = true,
            PreserveSubMeshEdges = true,
            PreserveBoneWeightSeams = true,
            PreserveSurfaceCurvature = true,
            PreserveSurfaceEnvelope = false,
            PreserveBilateralSymmetry = false,
            VertexPlacement = VertexPlacementMode.AvatarHybrid,
            FeatureAngleDegrees = 35f,
            MaxTriangleNormalDeviationDegrees = 45f,
            MaxSurfaceDeviation = 0.0,
            MaxBoundaryDeviation = 0.0,
            EnableSmartLink = true,
            VertexLinkDistance = double.Epsilon,
            MaxIterationCount = 150,
            Agressiveness = 7.0,
            ManualUVComponentCount = false,
            UVComponentCount = 2,
            MaxBoneWeightsPerVertex = 0,
            BoneWeightThreshold = 0f
        };

        /// <summary>
        /// If the border edges should be preserved.
        /// Default value: false
        /// </summary>
        [Tooltip("If the border edges should be preserved.")]
        public bool PreserveBorderEdges;
        /// <summary>
        /// If the UV seam edges should be preserved.
        /// Default value: true
        /// </summary>
        [Tooltip("If the UV seam edges should be preserved.")]
        public bool PreserveUVSeamEdges;
        /// <summary>
        /// If the UV foldover edges should be preserved.
        /// Default value: true
        /// </summary>
        [Tooltip("If the UV foldover edges should be preserved.")]
        public bool PreserveUVFoldoverEdges;
        /// <summary>
        /// If edges shared by different sub-meshes/material slots should be preserved.
        /// Default value: true
        /// </summary>
        [Tooltip("If edges shared by different sub-meshes/material slots should be preserved.")]
        public bool PreserveSubMeshEdges;
        /// <summary>
        /// If vertices with different skinning influences should be treated as protected seams.
        /// Default value: true
        /// </summary>
        [Tooltip("If vertices with different skinning influences should be treated as protected seams.")]
        public bool PreserveBoneWeightSeams;
        /// <summary>
        /// If the discrete curvature of the mesh surface be taken into account during simplification. Taking surface curvature into account can result in good quality mesh simplification, but it can slow the simplification process significantly.
        /// Default value: false
        /// </summary>
        [Tooltip("If the discrete curvature of the mesh surface be taken into account during simplification. Taking surface curvature into account can result in very good quality mesh simplification, but it can slow the simplification process significantly.")]
        public bool PreserveSurfaceCurvature;
        /// <summary>
        /// Keeps decimated triangles fitted to the immutable source envelope. Instead of rejecting a
        /// curved collapse merely because its new chords would cut inward, the collapse vertex is
        /// iteratively interpolated so edge midpoints and face centroids remain near the source surface.
        /// </summary>
        [Tooltip("Fits replacement triangles to the original source envelope to reduce inward shrinkage on hair, clothing shells, and other curved pieces.")]
        public bool PreserveSurfaceEnvelope;
        /// <summary>
        /// Automatically detects a bilateral X-axis mirror plane and blends collapse-envelope
        /// corrections with their mirrored counterpart. This keeps the anti-shrink interpolation
        /// balanced on matched left/right regions without forcing asymmetric accessories to mirror.
        /// </summary>
        [Tooltip("Balances collapse placement across automatically detected mirrored left/right regions.")]
        public bool PreserveBilateralSymmetry;
        /// <summary>
        /// Controls how the new position for an edge collapse is constrained. AvatarHybrid keeps smooth
        /// interior collapses on the local surface and keeps borders/high-curvature features on their edge.
        /// </summary>
        [Tooltip("Controls where collapsed vertices may be placed. Avatar Hybrid is recommended for skinned avatars.")]
        public VertexPlacementMode VertexPlacement;
        /// <summary>
        /// Edges whose adjacent face normals differ by at least this angle are treated as features by
        /// AvatarHybrid and use edge interpolation. Zero uses the compatibility default of 45 degrees.
        /// </summary>
        [Range(0f, 180f), Tooltip("Feature angle used by Avatar Hybrid placement. Lower values protect more small curvature details. Zero uses 45 degrees.")]
        public float FeatureAngleDegrees;
        /// <summary>
        /// Maximum allowed change between a surviving triangle's previous and proposed normal. Zero uses
        /// the compatibility default of 78 degrees. This remains a final safety rejection, not the primary
        /// detail-preservation mechanism.
        /// </summary>
        [Range(0f, 180f), Tooltip("Maximum triangle-normal change allowed by a collapse. Lower values preserve shape more strongly. Zero uses 78 degrees.")]
        public float MaxTriangleNormalDeviationDegrees;
        /// <summary>
        /// Maximum local-space distance allowed between newly formed surface samples and the immutable
        /// original surface in ReferenceAccurate mode. Zero uses 0.005% of the original bounds diagonal.
        /// </summary>
        [Min(0f), Tooltip("Maximum local-space surface deviation in Reference Accurate mode. Zero uses a conservative scale-relative automatic value.")]
        public double MaxSurfaceDeviation;
        /// <summary>
        /// Maximum local-space distance allowed between a newly formed open-boundary segment and the
        /// immutable original boundary curve in ReferenceAccurate mode. Zero uses 0.001% of the original bounds diagonal.
        /// </summary>
        [Min(0f), Tooltip("Maximum local-space boundary deviation in Reference Accurate mode. Zero uses a stricter scale-relative automatic value.")]
        public double MaxBoundaryDeviation;
        /// <summary>
        /// If a feature for smarter vertex linking should be enabled, reducing artifacts in the
        /// decimated result at the cost of a slightly more expensive initialization by treating vertices at
        /// the same position as the same vertex while separating the attributes.
        /// Default value: true
        /// </summary>
        [Tooltip("If a feature for smarter vertex linking should be enabled, reducing artifacts at the cost of slower simplification.")]
        public bool EnableSmartLink;
        /// <summary>
        /// The maximum distance between two vertices in order to link them.
        /// Note that this value is only used if EnableSmartLink is true.
        /// Default value: double.Epsilon
        /// </summary>
        [Tooltip("The maximum distance between two vertices in order to link them.")]
        public double VertexLinkDistance;
        /// <summary>
        /// The maximum iteration count. Higher number is more expensive but can bring you closer to your target quality.
        /// Sometimes a lower maximum count might be desired in order to lower the performance cost.
        /// Default value: 100
        /// </summary>
        [Tooltip("The maximum iteration count. Higher number is more expensive but can bring you closer to your target quality.")]
        public int MaxIterationCount;
        /// <summary>
        /// Controls how quickly the accepted error threshold grows. Higher values reach aggressive reductions sooner; lower values are more conservative but can require more iterations.
        /// Default value: 7.0
        /// </summary>
        [Tooltip("Controls how quickly the accepted error threshold grows. Lower values are more conservative; higher values simplify more aggressively.")]
        public double Agressiveness;
        /// <summary>
        /// If a manual UV component count should be used (set by UVComponentCount), instead of the automatic detection.
        /// Default value: false
        /// </summary>
        [Tooltip("If a manual UV component count should be used (set by UV Component Count below), instead of the automatic detection.")]
        public bool ManualUVComponentCount;
        /// <summary>
        /// The UV component count. The same UV component count will be used on all UV channels.
        /// Default value: 2
        /// </summary>
        [Range(0, 4), Tooltip("The UV component count. The same UV component count will be used on all UV channels.")]
        public int UVComponentCount;
        /// <summary>
        /// Maximum number of bone influences retained per vertex. A value of 0 preserves all
        /// influences supported by Unity (up to 255).
        /// Default value: 0.
        /// </summary>
        [Range(0, 255), Tooltip("Maximum bone influences retained per vertex. Zero preserves all influences.")]
        public int MaxBoneWeightsPerVertex;
        /// <summary>
        /// Bone influences at or below this weight are discarded before the remaining weights are normalized.
        /// Default value: 0.00001.
        /// </summary>
        [Min(0f), Tooltip("Bone influences at or below this weight are discarded before normalization.")]
        public float BoneWeightThreshold;
    }
}
