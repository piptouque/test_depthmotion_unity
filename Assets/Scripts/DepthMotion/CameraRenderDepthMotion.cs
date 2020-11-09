﻿using UnityEngine; 
using UnityEngine.Rendering; 
using UnityEngine.Assertions; 
using UnityEditor;
using System.IO;
using System;

namespace DepthMotion
{
    [RequireComponent(typeof(Camera))]
    public class CameraRenderDepthMotion : MonoBehaviour
    {
        public Vector2Int dim;
        [RangeAttribute(2, 8)]
        public int downscalingFactor;

        [RangeAttribute(1, 500)] public int samplingStep;
        
        #region MonoBehaviour Events

        void Awake()
        {
            AssertSystem();
            
            PrepareDims();
            PrepareTextures();
            PrepareCameras();
            
            CreateFileTree();
        }

        // OnPostRender only works with the legacy render pipeline
        void OnPostRender()
        {
            RenderDepthMotionStep();
        }

        // Kind of an equivalent for SRP.
        void EndCameraRendering(ScriptableRenderContext context, Camera cam)
        {
            if (cam == m_cam)
            {
                RenderDepthMotionStep();
            }
        }
        
        void OnDestroy()
        {
            FreeAll();
        }

        #endregion
        
        #region Private processing

        void RenderDepthMotionStep()
        {
            if (m_currentFrameIndex % samplingStep == 0)
            {
                RenderDepthMotion();
            }
            ++m_currentFrameIndex;
        }
        
        void RenderDepthMotion()
        {
            SaveFrame();
        }

        void FreeAll()
        {
            m_view.Release();
            m_motion.Release();
            m_depth.Release();

            RenderPipelineManager.endCameraRendering -= EndCameraRendering;
        }
        
        void PrepareDims()
        {
            m_downscaledDim = dim / downscalingFactor;
        }
        void PrepareCameras()
        {
            m_cam = GetComponent<Camera>();
            m_cam.allowDynamicResolution = false;

            m_downscaledCameraObject = new GameObject();
            m_downscaledCamera = m_downscaledCameraObject.AddComponent<Camera>();
            
            m_downscaledCamera.CopyFrom(m_cam);
            m_downscaledCamera.depthTextureMode = DepthTextureMode.Depth | DepthTextureMode.MotionVectors;
            
            m_downscaledCamera.transform.SetParent(gameObject.transform);
            m_downscaledCameraObject.SetActive(false);
            // Add callback on end of rendering of frame.
            // Used by Custom Rendering Piplines,
            // as a stand-in for OnPostRender.
            // RenderPipelineManager.endCameraRendering += EndCameraRendering;
            
            // For now will use not Shader Replacement.
            m_blitViewCommand = new CommandBuffer   { name = "view" };
            m_blitDepthCommand  = new CommandBuffer { name = "depth" };
            m_blitMotionCommand = new CommandBuffer { name = "motion" };

            m_viewId   = Shader.PropertyToID(c_viewTag);
            m_depthId  = Shader.PropertyToID(c_depthTag);
            m_motionId = Shader.PropertyToID(c_motionTag);
            
            m_blitViewCommand.GetTemporaryRT(m_viewId, -1, -1, 0);
            m_blitDepthCommand.GetTemporaryRT(m_depthId, m_downscaledDim.x, m_downscaledDim.y, 0);
            m_blitMotionCommand.GetTemporaryRT(m_motionId, m_downscaledDim.x, m_downscaledDim.y, 0);
            
            m_blitViewCommand.SetGlobalTexture(c_viewTag, m_viewId);
            m_blitDepthCommand.SetGlobalTexture(c_depthTag, m_depthId);
            m_blitMotionCommand.SetGlobalTexture(c_motionTag, m_motionId);
            
            /*
            // should set each render target as active for the corresponding buffer
            // then signal that the latter should render in its active target.
            
            m_blitViewCommand.SetRenderTarget(m_view);
            m_blitDepthCommand.SetRenderTarget(m_depth);
            m_blitMotionCommand.SetRenderTarget(m_motion);

            // they actually included the english spelling for 'grey'. wow.
            m_blitViewCommand.ClearRenderTarget(true, true, Color.grey);
            m_blitDepthCommand.ClearRenderTarget(true, true, Color.green);
            m_blitMotionCommand.ClearRenderTarget(true, true, Color.blue);
            */

            m_blitViewCommand.Blit(
                BuiltinRenderTextureType.MotionVectors, 
                m_viewId
                );
            m_blitDepthCommand.Blit(
                BuiltinRenderTextureType.Depth, 
                m_depthId
                );
            m_blitMotionCommand.Blit(
                BuiltinRenderTextureType.MotionVectors, 
                m_motionId
                );
            
            // TODO: figure out at which CameraEvent we should get the motion vectors.
            // They seem to be available as soon as CameraEvent.BeforeForwardAlpha,
            // But they don't show anything..
            m_cam.AddCommandBuffer(
                CameraEvent.AfterEverything,
                m_blitViewCommand
                );
            m_downscaledCamera.AddCommandBuffer(
                CameraEvent.AfterDepthTexture,
                m_blitDepthCommand
                );
            m_downscaledCamera.AddCommandBuffer(
                CameraEvent.AfterEverything,
                m_blitMotionCommand
                );
            
            // Shader Replacement part.
            /*
            m_cam.targetTexture = m_view;
            m_downscaledCameraObject = new GameObject();
            m_downscaledCameraObject.SetActive(false);
            m_downscaledCamera = m_downscaledCameraObject.AddComponent<Camera>();
            */
            
        }

        void PrepareTextures()
        {
            m_view = new RenderTexture(
                dim.x, 
                dim.y, 
                0,
                RenderTextureFormat.ARGB32
                );

            m_motion = new RenderTexture(
                m_downscaledDim.x, 
                m_downscaledDim.y, 
                0,
                RenderTextureFormat.RG16
            );
            
            m_depth = new RenderTexture(
                m_downscaledDim.x, 
                m_downscaledDim.y, 
                16,
                RenderTextureFormat.ARGB32
                );

            m_view.Create();
            m_motion.Create();
            m_depth.Create();
            
            m_texture2D = new Texture2D(
                dim.x,
                dim.y, 
                TextureFormat.RGB24, 
                false, 
                false
                );
            m_downscaledTexture2D = new Texture2D(
                m_downscaledDim.x, 
                m_downscaledDim.y,
                TextureFormat.RGB24,
                false,
                false
                );
        }

        void AssertSystem()
        {
            Assert.IsTrue(SystemInfo.supportsMotionVectors);
        }
        #endregion

        #region Saving routine
        
        void CreateFileTree()
        {
            if (!DoesFolderExist(c_outputRootDir))
            {
               CreateFolder("Editor", c_outputRootDir); 
            }
            string timeStamp = DateTime.Now.ToString("yy_MM_dd_hhmmss");

            string outputRootPath = Path.Combine("Editor", c_outputRootDir);
            m_outputPath = CreateFolder(outputRootPath, timeStamp);


            CreateFolder(m_outputPath, c_viewDir);
            CreateFolder(m_outputPath, c_depthDir);
            CreateFolder(m_outputPath, c_motionDir);
        }

        void SaveFrame()
        {
            SaveTexture(
                m_view,  
                m_texture2D, 
                Path.Combine(m_outputPath, c_viewDir),
                m_currentFrameIndex.ToString()
            );

            SaveTexture(
                m_depth,
                m_downscaledTexture2D,
                Path.Combine(m_outputPath, c_depthDir),
                m_currentFrameIndex.ToString()
            );
            
            SaveTexture(
                m_motion,  
                m_downscaledTexture2D, 
                Path.Combine(m_outputPath, c_viewDir),
                m_currentFrameIndex.ToString()
            );
        }

        static void SaveTexture(RenderTexture renderTexture, Texture2D texture2D, string dirPath, string fileName)
        {
            const string extension = ".png";
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = renderTexture;
            texture2D.ReadPixels(new Rect(0, 0, renderTexture.width, renderTexture.height), 0, 0);
            texture2D.Apply();
            RenderTexture.active = previous;
            CreateFile(texture2D.EncodeToPNG(), dirPath, fileName, extension);
        }
        

        #endregion

        #region IO utility


        /**
         * @returns System path of root dir (child of Project/ dir)
         * @warning Only works in Editor mode.
         */
        static string GetProjectRootFolderPath(string rootDir)
        {
            return Path.Combine(Path.GetDirectoryName(Application.dataPath), rootDir);
        }

        /**
         * Create a folder in Project from root directory.
         * 
         * @param parentPath Name of the parent path from Project root.
         * @param dir Name of the folder to create.
         *
         * @returns Path from Project root of newly created directory.
         */
        static string CreateFolder(string parentPath, string dir)
        {
            Directory.CreateDirectory(Path.Combine(GetProjectRootFolderPath(parentPath), dir));
            return Path.Combine(parentPath, dir);
        }

        /**
         * Checks whether the Project directory exists.
         * @param dirPath Path of the dir from Project root.
         */
        static bool DoesFolderExist(string dirPath)
        {
            return Directory.Exists(GetProjectRootFolderPath(dirPath));
        }

        /**
         * Create a file and write data to it.
         * @param filePath Path of file from Project root.
         * @param data Byte array of data.
         */
        static void CreateFile(byte[] data, string dirPath, string fileName, string extension = "")
        {
            File.WriteAllBytes(
                GetProjectRootFolderPath(Path.Combine(dirPath, Path.ChangeExtension(fileName, extension))),
                data
                );
            
        }

        #endregion
        
        #region Private data
        Vector2Int m_downscaledDim;
        GameObject m_downscaledCameraObject;
        Camera m_cam;
        Camera m_downscaledCamera;

        /**
         * Downscaled motion texture.
         */
        RenderTexture m_motion;
        /**
         * Downscaled motion texture.
         */
        RenderTexture m_depth;
        /**
         * Original scene texture.
         */
        RenderTexture m_view;

        int m_motionId;
        int m_depthId;
        int m_viewId;
        

        CommandBuffer m_blitViewCommand;
        CommandBuffer m_blitDepthCommand;
        CommandBuffer m_blitMotionCommand;

        int m_currentFrameIndex;
        Texture2D m_texture2D;
        Texture2D m_downscaledTexture2D;

        const string c_viewTag   = "_Depth";
        const string c_depthTag  = "_View";
        const string c_motionTag = "_Motion";

        string m_outputPath;

        const string c_outputRootDir = "OutputData";
        const string c_viewDir       = "View";
        const string c_motionDir     = "Motion";
        const string c_depthDir      = "Depth";

        #endregion
    }
}