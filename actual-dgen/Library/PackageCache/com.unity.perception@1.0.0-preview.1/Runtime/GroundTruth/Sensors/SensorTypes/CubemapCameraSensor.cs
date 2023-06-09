using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Perception.GroundTruth.Sensors.Channels;
using UnityEngine.Perception.GroundTruth.Utilities;
using UnityEngine.Rendering;
using UnityEngine.Scripting.APIUpdating;

namespace UnityEngine.Perception.GroundTruth.Sensors
{
#if PERCEPTION_EXPERIMENTAL
    /// <summary>
    /// The base class for <see cref="CameraSensor"/>s that generate projection views from a captured cubemap.
    /// </summary>
    [Serializable]
    [MovedFrom("UnityEngine.Perception.GroundTruth.Internal")]
    public abstract class CubemapCameraSensor : CameraSensor
    {
        static readonly int k_TempSuperResRGBTexture = Shader.PropertyToID("_TempSuperResRGBTexture");

#if HDRP_PRESENT
        CameraSensorHdrpPass[] m_CameraSensorPasses = new CameraSensorHdrpPass[6];
#endif

        /// <summary>
        /// The 6 cubemap-face-cameras that will be used to generate an omnidirectional cubemap.
        /// </summary>
        Camera[] m_CubemapFaceCameras = new Camera[6];

        /// <summary>
        /// A mapping between each enabled channel and its associated cubemap texture.
        /// </summary>
        Dictionary<CameraChannelBase, RenderTexture> m_ChannelCubemapTextures = new();

        /// <summary>
        /// A mapping between each enabled channel and its associated cubemap face textures.
        /// </summary>
        Dictionary<CameraChannelBase, RenderTexture[]> m_ChannelCubeFaceTextures = new();

        /// <summary>
        /// The resolution of the cubemap capture generated by this sensor.
        /// </summary>
        public CubemapResolution cubemapResolution = CubemapResolution._512x512;

        /// <summary>
        /// Average together multiple cubemap samples per pixel to create a smoother output less aliased output.
        /// Note that this field will not influence the final output resolution of this sensor.
        /// </summary>
        [Tooltip("Average together multiple cubemap samples per pixel to create a smoother output less aliased output. " +
            "Note that this field will not influence the final output resolution of this sensor.")]
        public SuperSamplingFactor cubemapSuperSampling = SuperSamplingFactor.None;

        /// <summary>
        /// The integer width of a cubemap generated by this sensor.
        /// </summary>
        public int cubemapWidth => (int)cubemapResolution;

        /// <inheritdoc/>
        public override int pixelWidth => perceptionCamera.attachedCamera.pixelWidth;

        /// <inheritdoc/>
        public override int pixelHeight => perceptionCamera.attachedCamera.pixelHeight;

        /// <summary>
        /// The pixel width of the super resolution sensor.
        /// </summary>
        public int superResWidth => perceptionCamera.attachedCamera.pixelWidth * (int)cubemapSuperSampling;

        /// <summary>
        /// The pixel height of the super resolution sensor.
        /// </summary>
        public int superResHeight => perceptionCamera.attachedCamera.pixelWidth * (int)cubemapSuperSampling;

        /// <summary>
        /// Renders a custom projection from the captured cubemap output and channel textures.
        /// </summary>
        /// <param name="cmd">The commandBuffer to enqueue graphics commands to execute this operation.</param>
        /// <param name="inputCubemapTexture">The input cubemap texture to project from.</param>
        /// <param name="outputChannelTexture">The output channel texture to render to.</param>
        protected abstract void ProjectFisheyeFromCubemap(
            CommandBuffer cmd,
            RenderTexture inputCubemapTexture,
            RenderTargetIdentifier outputChannelTexture);

        /// <inheritdoc/>
        protected override void Setup()
        {
            m_CubemapFaceCameras = CubemapUtility.CreateCubemapCameras(
                perceptionCamera.transform, perceptionCamera.attachedCamera);

            for (var i = 0; i < m_CubemapFaceCameras.Length; i++)
            {
                var targetCamera = m_CubemapFaceCameras[i];
#if HDRP_PRESENT
                var pass = new CameraSensorHdrpPass(targetCamera);
                m_CameraSensorPasses[i] = pass;
#endif
            }

            CameraUtilities.ConvertToPassThroughCamera(perceptionCamera.attachedCamera);
        }

        /// <inheritdoc/>
        protected override void Cleanup()
        {
            foreach (var texture in m_ChannelCubemapTextures.Values)
                if (texture != null)
                    texture.Release();
            foreach (var cubeFaceTextures in m_ChannelCubeFaceTextures.Values)
                foreach (var texture in cubeFaceTextures)
                    if (texture != null)
                        texture.Release();
        }

        /// <inheritdoc/>
        protected override RenderTexture SetupChannel<T>(T channel)
        {
            var channelName = typeof(T).Name;

            // Create new cube face textures for each of the cubemap cameras to render to.
            var cubeFaceTextures = new RenderTexture[6];
            m_ChannelCubeFaceTextures[channel] = cubeFaceTextures;
            for (var i = 0; i < m_CubemapFaceCameras.Length; i++)
            {
                var cubemapFaceTexture = channel is IPostProcessChannel postProcessChannel
                    ? postProcessChannel.CreatePreprocessTexture(cubemapWidth, cubemapWidth)
                    : channel.CreateOutputTexture(cubemapWidth, cubemapWidth);
                cubemapFaceTexture.name = $"{channelName} Cube Face: {CubemapUtility.cameraDirectionNames[i]}";
                m_CameraSensorPasses[i].AddChannel(channel, cubemapFaceTexture);
                cubeFaceTextures[i] = cubemapFaceTexture;
                if (channel is RGBChannel)
                    m_CubemapFaceCameras[i].targetTexture = cubemapFaceTexture;
            }

            // Create the channel's output texture.
            var channelOutputTexture = channel is RGBChannel && perceptionCamera.attachedCamera.targetTexture != null
                ? perceptionCamera.attachedCamera.targetTexture : channel.CreateOutputTexture(pixelWidth, pixelHeight);

            // Create a preprocess texture if the channel requires post processing.
            var referenceTexture = channelOutputTexture;
            if (channel is IPostProcessChannel postChannel)
            {
                referenceTexture = postChannel.CreatePreprocessTexture(pixelWidth, pixelHeight);
                postChannel.preprocessTexture = referenceTexture;
            }

            // Create the cubemap texture that the outputs of the 6 cubemap cameras will be copied to.
            var cubemapTexture = new RenderTexture(
                cubemapWidth, cubemapWidth, 32, referenceTexture.graphicsFormat, 0)
            {
                name = $"{channelName} Cubemap",
                dimension = TextureDimension.Cube,
                filterMode = referenceTexture.filterMode
            };
            cubemapTexture.Create();
            m_ChannelCubemapTextures[channel] = cubemapTexture;

            return channelOutputTexture;
        }

        /// <inheritdoc/>
        protected override void OnEndFrameRendering(ScriptableRenderContext ctx)
        {
            var camera = perceptionCamera.attachedCamera;
            var cmd = CommandBufferPool.Get($"{camera.name} Cubemap");

            // Combine each camera's channel target textures into cubemap textures.
            foreach (var pair in m_ChannelCubeFaceTextures)
            {
                var cubeFaceTextures = pair.Value;
                var cubemapTexture = m_ChannelCubemapTextures[pair.Key];
                for (var i = 0; i < cubeFaceTextures.Length; i++)
                {
                    cmd.SetRenderTarget(cubeFaceTextures[i]);
                    cmd.CopyTexture(
                        cubeFaceTextures[i], 0, 0, 0, 0, cubemapWidth, cubemapWidth,
                        cubemapTexture, i, 0, 0, 0);
                }
                if (pair.Key is VertexNormalsChannel)
                    cmd.Blit(cubeFaceTextures[0], (RenderTexture)null);
            }

            // Project the cubemap textures into each channel's output texture.
            foreach (var pair in m_ChannelCubemapTextures)
            {
                var channel = pair.Key;
                var cubemapTexture = pair.Value;

                if (channel is RGBChannel rgbChannel && cubemapSuperSampling != SuperSamplingFactor.None)
                {
                    cmd.SetRenderTarget(rgbChannel.outputTexture);
                    cmd.ClearRenderTarget(true, true, rgbChannel.clearColor);

                    cmd.GetTemporaryRT(k_TempSuperResRGBTexture, superResWidth, superResHeight, 32,
                        FilterMode.Bilinear, GraphicsFormat.R8G8B8A8_SRGB, 1, true);
                    ProjectFisheyeFromCubemap(cmd, cubemapTexture, k_TempSuperResRGBTexture);

                    // Down-sample the RGB texture by averaging super resolution pixels in a compute shader.
                    SuperSamplingUtility.Downscale(
                        cmd, k_TempSuperResRGBTexture, rgbChannel.outputTexture,
                        pixelWidth, pixelHeight, cubemapSuperSampling);

                    cmd.ReleaseTemporaryRT(k_TempSuperResRGBTexture);
                }
                else
                {
                    var outputTexture = channel is IPostProcessChannel postProcessChannel
                        ? postProcessChannel.preprocessTexture : channel.outputTexture;
                    cmd.SetRenderTarget(channel.outputTexture);
                    cmd.ClearRenderTarget(true, true, channel.clearColor);
                    ProjectFisheyeFromCubemap(cmd, cubemapTexture, outputTexture);
                }
            }

            // Execute post processing step for post-process-channels.
            foreach (var channel in perceptionCamera.channels)
            {
                if (channel is IPostProcessChannel postProcessChannel)
                {
                    var sampleName = $"Post Process {channel.GetType().Name}";
                    cmd.BeginSample(sampleName);
                    postProcessChannel.PostProcessChannelOutput(
                        ctx, cmd, postProcessChannel.preprocessTexture, channel.outputTexture);
                    cmd.EndSample(sampleName);
                }
            }

            // Reset the render target back to the backbuffer.
            cmd.SetRenderTarget((RenderTexture)null);

            ctx.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
            ctx.Submit();
        }
    }
#endif
}
