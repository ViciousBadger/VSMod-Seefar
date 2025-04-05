using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace Farseer;

public class FarRegionRenderer : IRenderer
{
    public struct PerModelData
    {
        public FarRegionData SourceData { get; set; }
        public Vec3d Position { get; set; }
        public MeshRef MeshRef { get; set; }
    }

    public double RenderOrder => 0.36;

    public int RenderRange => 9999;

    private ICoreClientAPI capi;
    private int farViewDistance;
    private Dictionary<long, PerModelData> activeRegionModels = new Dictionary<long, PerModelData>();

    private Matrixf modelMat = new Matrixf();
    private float[] projectionMat = Mat4f.Create();
    private IShaderProgram prog;

    private Vec3f mainColor;

    public FarRegionRenderer(ICoreClientAPI capi, int farViewDistance)
    {
        this.capi = capi;
        this.farViewDistance = farViewDistance;

        capi.Event.ReloadShader += LoadShader;
        LoadShader();

        capi.Event.RegisterRenderer(this, EnumRenderStage.Opaque);

        // From sky.png
        mainColor = new Vec3f(0.525f, 0.620f, 0.776f);

        var farFog = new AmbientModifier().EnsurePopulated();
        farFog.FogDensity = new WeightedFloat(0.005f, 1.0f);
        farFog.FogMin = new WeightedFloat(0.0f, 1.0f);
        farFog.FogColor = new WeightedFloatArray(new float[] {
                mainColor.X,mainColor.Y,mainColor.Z
            }, 1.0f);

        //capi.Ambient.CurrentModifiers.Add("farfog", farFog);
    }

    public bool LoadShader()
    {
        prog = capi.Shader.NewShaderProgram();

        prog.AssetDomain = "farseer";
        prog.VertexShader = capi.Shader.NewShader(EnumShaderType.VertexShader);
        prog.FragmentShader = capi.Shader.NewShader(EnumShaderType.FragmentShader);

        capi.Shader.RegisterFileShaderProgram("region", prog);

        var result = prog.Compile();

        return result;
    }

    private long RegionNeighbourIndex(long idx, int offsetX, int offsetZ, int regionMapSize)
    {
        int rX = (int)(idx % regionMapSize);
        int rZ = (int)(idx / regionMapSize);

        return (long)(rZ + offsetZ) * (long)regionMapSize + (long)(rX + offsetX);
    }

    public void BuildRegion(FarRegionData sourceData, bool isRebuild = false)
    {
        // Find neighbour id's for stitching
        var eastIdx = RegionNeighbourIndex(sourceData.RegionIndex, 1, 0, sourceData.RegionMapSize);
        var southIdx = RegionNeighbourIndex(sourceData.RegionIndex, 0, 1, sourceData.RegionMapSize);
        var southEastIdx = RegionNeighbourIndex(sourceData.RegionIndex, 1, 1, sourceData.RegionMapSize);

        var mesh = new MeshData(false);

        var gridSize = sourceData.Heightmap.GridSize;
        float cellSize = sourceData.RegionSize / (float)gridSize;

        var vertexCount = (gridSize + 1) * (gridSize + 1);
        mesh.SetVerticesCount(vertexCount);
        mesh.xyz = new float[vertexCount * 3];

        var indicesCount = gridSize * gridSize * 6;
        mesh.SetIndicesCount(indicesCount);
        mesh.Indices = new int[indicesCount]; // 2 triangles per cell, 3 indices per triangle

        int xyz = 0;
        for (int vZ = 0; vZ <= gridSize; vZ++)
        {
            for (int vX = 0; vX <= gridSize; vX++)
            {
                mesh.xyz[xyz++] = vX * cellSize;

                int sample = 0;

                if (vX == gridSize && vZ == gridSize && activeRegionModels.TryGetValue(southEastIdx, out PerModelData southEastData))
                {
                    // For corner, select north-western-most point south-east neighbour 
                    sample = southEastData.SourceData.Heightmap.Points[0];
                }
                else if (vX == gridSize && vZ < gridSize && activeRegionModels.TryGetValue(eastIdx, out PerModelData eastData))
                {
                    // For x end, select west-most point of east neighbour
                    sample = eastData.SourceData.Heightmap.Points[vZ * gridSize];
                }
                else if (vZ == gridSize && vX < gridSize && activeRegionModels.TryGetValue(southIdx, out PerModelData southData))
                {
                    // For z end, select north-most point of south neighbour
                    sample = southData.SourceData.Heightmap.Points[vX];
                }
                else
                {
                    // If no neighbour was sampled, use source data, but clamp to heightmap size.
                    int vXLimited = Math.Min(vX, gridSize - 1);
                    int vZLimited = Math.Min(vZ, gridSize - 1);
                    sample = sourceData.Heightmap.Points[vZLimited * gridSize + vXLimited];
                }

                mesh.xyz[xyz++] = sample;
                mesh.xyz[xyz++] = vZ * cellSize;
            }
        }

        int index = 0;
        for (int i = 0; i < gridSize; i++)
        {
            for (int j = 0; j < gridSize; j++)
            {
                // First triangle of the cell
                mesh.Indices[index++] = i * (gridSize + 1) + j;           // Top-left
                mesh.Indices[index++] = (i + 1) * (gridSize + 1) + j;     // Bottom-left
                mesh.Indices[index++] = i * (gridSize + 1) + j + 1;       // Top-right

                // Second triangle of the cell
                mesh.Indices[index++] = i * (gridSize + 1) + j + 1;       // Top-right
                mesh.Indices[index++] = (i + 1) * (gridSize + 1) + j;     // Bottom-left
                mesh.Indices[index++] = (i + 1) * (gridSize + 1) + j + 1; // Bottom-right
            }
        }

        if (activeRegionModels.TryGetValue(sourceData.RegionIndex, out PerModelData existingData))
        {
            existingData.MeshRef.Dispose();
            activeRegionModels.Remove(sourceData.RegionIndex);
        }

        activeRegionModels.Add(sourceData.RegionIndex, new PerModelData()
        {
            SourceData = sourceData,
            Position = new Vec3d(
                    sourceData.RegionX * sourceData.RegionSize,
                    0.0f,
                    sourceData.RegionZ * sourceData.RegionSize
                    ),
            MeshRef = capi.Render.UploadMesh(mesh),
        });

        if (!isRebuild)
        {
            // Re-build neighbours that are affected by this new data.
            var westIdx = RegionNeighbourIndex(sourceData.RegionIndex, -1, 0, sourceData.RegionMapSize);
            var northIdx = RegionNeighbourIndex(sourceData.RegionIndex, 0, -1, sourceData.RegionMapSize);
            var northWestIdx = RegionNeighbourIndex(sourceData.RegionIndex, -1, -1, sourceData.RegionMapSize);


            if (activeRegionModels.TryGetValue(northIdx, out PerModelData northData))
            {
                BuildRegion(northData.SourceData, true);
            }
            if (activeRegionModels.TryGetValue(westIdx, out PerModelData westData))
            {
                BuildRegion(westData.SourceData, true);
            }
            if (activeRegionModels.TryGetValue(northWestIdx, out PerModelData northWestData))
            {
                BuildRegion(northWestData.SourceData, true);
            }
        }
    }

    public void UnloadRegion(long regionIdx)
    {
        if (activeRegionModels.TryGetValue(regionIdx, out PerModelData model))
        {
            model.MeshRef.Dispose();
            activeRegionModels.Remove(regionIdx);
        }
    }

    public void ClearLoadedRegions()
    {
        foreach (var regionModel in activeRegionModels.Values)
        {
            regionModel.MeshRef.Dispose();
        }
        activeRegionModels.Clear();
    }

    public void Dispose()
    {
        ClearLoadedRegions();
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {

        var rapi = capi.Render;
        if (rapi.FrameWidth == 0) return;

        Vec3d camPos = capi.World.Player.Entity.CameraPos;

        // Prevent far terrain from being cut off at zfar
        //
        //
        var fov = (float)ClientSettings.FieldOfView * ((float)Math.PI / 180f);
        rapi.Set3DProjection(farViewDistance, fov);

        var viewDistance = (float)capi.World.Player.WorldData.DesiredViewDistance;

        // var fov = (float)ClientSettings.FieldOfView * ((float)Math.PI / 180f);
        // float num = (float)rapi.FrameWidth / (float)rapi.FrameHeight;
        // Mat4f.Perspective(projectionMat, fov, num, 32f, farViewDistance);

        foreach (var regionModel in activeRegionModels.Values)
        {

            prog.Use();

            modelMat.Identity()
                .Translate(regionModel.Position.X, regionModel.Position.Y, regionModel.Position.Z)
                .Translate(-camPos.X, -camPos.Y, -camPos.Z);

            prog.UniformMatrix("modelMatrix", modelMat.Values);
            prog.UniformMatrix("viewMatrix", rapi.CameraMatrixOriginf);
            //prog.UniformMatrix("projectionMatrix", projectionMat);
            prog.UniformMatrix("projectionMatrix", rapi.CurrentProjectionMatrix);

            prog.Uniform("mainColor", mainColor);

            prog.Uniform("sunPosition", capi.World.Calendar.SunPositionNormalized);
            prog.Uniform("sunColor", capi.World.Calendar.SunColor);
            prog.Uniform("dayLight", Math.Max(0, capi.World.Calendar.DayLightStrength - capi.World.Calendar.MoonLightStrength * 0.95f));

            prog.Uniform("rgbaFogIn", capi.Ambient.BlendedFogColor);
            prog.Uniform("fogDensityIn", capi.Ambient.BlendedFogDensity);
            prog.Uniform("fogMinIn", capi.Ambient.BlendedFogMin);
            prog.Uniform("horizonFog", capi.Ambient.BlendedCloudDensity);
            prog.Uniform("flatFogDensity", capi.Ambient.BlendedFlatFogDensity);
            prog.Uniform("flatFogStart", capi.Ambient.BlendedFlatFogYPosForShader - (float)capi.World.Player.Entity.CameraPos.Y);

            prog.Uniform("viewDistance", viewDistance);
            prog.Uniform("farViewDistance", (float)this.farViewDistance);

            rapi.RenderMesh(regionModel.MeshRef);

            prog.Stop();
        }
        rapi.Reset3DProjection();
    }
}
