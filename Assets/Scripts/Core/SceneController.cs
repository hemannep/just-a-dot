using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.Events;

namespace JustADot.Core
{
    /// <summary>
    /// Advanced scene management system with transitions, loading screens, and memory optimization
    /// </summary>
    public class SceneController : MonoBehaviour
    {
        #region Enums & Constants
        public enum TransitionType
        {
            None,
            Fade,
            CircleWipe,
            DiamondWipe,
            SlideLeft,
            SlideRight,
            SlideUp,
            SlideDown,
            CrossFade,
            Pixelate,
            Dissolve
        }

        public enum SceneName
        {
            Splash = 0,
            Home = 1,
            Levels = 2,
            Gameplay = 3,
            Settings = 4
        }

        private const string TRANSITION_CANVAS_NAME = "SceneTransitionCanvas";
        private const string LOADING_CANVAS_NAME = "LoadingCanvas";
        private const float DEFAULT_TRANSITION_DURATION = 0.5f;
        private const float MIN_LOADING_TIME = 0.3f; // Minimum time to show loading screen
        private const float MEMORY_CLEANUP_THRESHOLD = 0.8f; // 80% memory usage triggers cleanup
        #endregion

        #region Events
        public static UnityEvent<string> OnSceneLoadStart = new UnityEvent<string>();
        public static UnityEvent<string> OnSceneLoadComplete = new UnityEvent<string>();
        public static UnityEvent<float> OnLoadProgress = new UnityEvent<float>();
        public static UnityEvent<string, string> OnSceneTransition = new UnityEvent<string, string>();
        public static UnityEvent OnTransitionComplete = new UnityEvent();
        public static UnityEvent<string> OnSceneLoadError = new UnityEvent<string>();
        #endregion

        #region Serialized Fields
        [Header("Transition Settings")]
        [SerializeField] private TransitionType defaultTransition = TransitionType.Fade;
        [SerializeField] private float defaultTransitionDuration = 0.5f;
        [SerializeField] private AnimationCurve transitionCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);
        [SerializeField] private Color transitionColor = Color.black;
        
        [Header("Loading Screen Settings")]
        [SerializeField] private bool useLoadingScreen = true;
        [SerializeField] private float minimumLoadingTime = 0.5f;
        [SerializeField] private bool showLoadingProgress = true;
        [SerializeField] private bool showLoadingTips = true;
        
        [Header("Performance Settings")]
        [SerializeField] private bool unloadUnusedAssets = true;
        [SerializeField] private bool forceGarbageCollection = true;
        [SerializeField] private float asyncLoadPriority = 0.9f;
        
        [Header("Scene Configuration")]
        [SerializeField] private List<SceneData> sceneDatabase = new List<SceneData>();
        [SerializeField] private bool allowSceneReload = true;
        #endregion

        #region Private Variables
        // Scene Management
        private string currentSceneName;
        private string previousSceneName;
        private Scene currentScene;
        private AsyncOperation currentLoadOperation;
        private Queue<SceneLoadRequest> loadQueue;
        private bool isTransitioning;
        private bool isLoading;
        private Dictionary<string, SceneData> sceneDataCache;
        
        // UI Components
        private Canvas transitionCanvas;
        private Image transitionImage;
        private Canvas loadingCanvas;
        private Slider loadingProgressBar;
        private Text loadingPercentText;
        private Text loadingTipText;
        private GameObject loadingSpinner;
        
        // Transition Components
        private Material transitionMaterial;
        private Coroutine activeTransition;
        private float currentTransitionProgress;
        
        // Performance Tracking
        private float sceneLoadStartTime;
        private Dictionary<string, float> sceneLoadTimes;
        private List<AsyncOperation> activeOperations;
        private float lastMemoryCheckTime;
        
        // Loading Tips
        private List<string> loadingTips = new List<string>
        {
            "Tip: Each level has a unique solution!",
            "Tip: Some levels require device sensors",
            "Tip: Perfect completions unlock special rewards",
            "Tip: Try different approaches if you're stuck",
            "Tip: Hints are available after watching an ad",
            "Tip: Complete themes to unlock new dot colors",
            "Tip: Some levels are time-sensitive",
            "Tip: Your phone's settings can affect gameplay",
            "Tip: Motion levels require gentle movements",
            "Tip: Audio levels use your microphone"
        };
        #endregion

        #region Data Classes
        [Serializable]
        public class SceneData
        {
            public string sceneName;
            public int buildIndex;
            public TransitionType preferredTransition;
            public float customTransitionDuration;
            public bool requiresLoading;
            public bool allowAdditive;
            public string displayName;
            public Sprite sceneIcon;
            public AudioClip sceneMusic;
            [TextArea(2, 4)]
            public string sceneDescription;
        }

        private class SceneLoadRequest
        {
            public string sceneName;
            public TransitionType transition;
            public float duration;
            public LoadSceneMode loadMode;
            public Action<bool> callback;
            public Dictionary<string, object> sceneParameters;
            public float priority;
        }

        [Serializable]
        public class SceneTransitionSettings
        {
            public TransitionType type = TransitionType.Fade;
            public float duration = 0.5f;
            public AnimationCurve curve = AnimationCurve.EaseInOut(0, 0, 1, 1);
            public Color color = Color.black;
            public bool useLoadingScreen = true;
            public Dictionary<string, object> parameters;
        }
        #endregion

        #region Unity Lifecycle
        private void Awake()
        {
            InitializeSceneController();
            DontDestroyOnLoad(gameObject);
        }

        private void Start()
        {
            // Cache current scene
            currentScene = SceneManager.GetActiveScene();
            currentSceneName = currentScene.name;
            
            // Setup scene loaded callback
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            
            StartCoroutine(MemoryMonitorRoutine());
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            
            if (activeTransition != null)
            {
                StopCoroutine(activeTransition);
            }
            
            CleanupTransitionComponents();
        }
        #endregion

        #region Initialization
        private void InitializeSceneController()
        {
            // Initialize data structures
            loadQueue = new Queue<SceneLoadRequest>();
            sceneDataCache = new Dictionary<string, SceneData>();
            sceneLoadTimes = new Dictionary<string, float>();
            activeOperations = new List<AsyncOperation>();
            
            // Cache scene data
            foreach (var sceneData in sceneDatabase)
            {
                if (!string.IsNullOrEmpty(sceneData.sceneName))
                {
                    sceneDataCache[sceneData.sceneName] = sceneData;
                }
            }
            
            // Create UI components
            CreateTransitionCanvas();
            CreateLoadingCanvas();
            
            Debug.Log("[SceneController] Initialized successfully");
        }

        private void CreateTransitionCanvas()
        {
            if (transitionCanvas != null) return;
            
            GameObject canvasGO = new GameObject(TRANSITION_CANVAS_NAME);
            canvasGO.transform.SetParent(transform);
            
            // Setup canvas
            transitionCanvas = canvasGO.AddComponent<Canvas>();
            transitionCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            transitionCanvas.sortingOrder = 9999; // Always on top
            
            // Add canvas scaler for responsive design
            CanvasScaler scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            
            canvasGO.AddComponent<GraphicRaycaster>();
            
            // Create transition image
            GameObject imageGO = new GameObject("TransitionImage");
            imageGO.transform.SetParent(canvasGO.transform, false);
            
            transitionImage = imageGO.AddComponent<Image>();
            transitionImage.color = new Color(transitionColor.r, transitionColor.g, transitionColor.b, 0);
            transitionImage.raycastTarget = true; // Block input during transition
            
            // Setup rect transform to cover entire screen
            RectTransform rect = imageGO.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.sizeDelta = Vector2.zero;
            rect.anchoredPosition = Vector2.zero;
            
            // Initially hide
            canvasGO.SetActive(false);
        }

        private void CreateLoadingCanvas()
        {
            if (loadingCanvas != null) return;
            
            GameObject canvasGO = new GameObject(LOADING_CANVAS_NAME);
            canvasGO.transform.SetParent(transform);
            
            // Setup canvas
            loadingCanvas = canvasGO.AddComponent<Canvas>();
            loadingCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            loadingCanvas.sortingOrder = 9998; // Below transition but above everything else
            
            // Add canvas scaler
            CanvasScaler scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            
            canvasGO.AddComponent<GraphicRaycaster>();
            
            // Create loading UI elements
            CreateLoadingUI(canvasGO);
            
            // Initially hide
            canvasGO.SetActive(false);
        }

        private void CreateLoadingUI(GameObject parent)
        {
            // Background
            GameObject bgGO = new GameObject("Background");
            bgGO.transform.SetParent(parent.transform, false);
            Image bg = bgGO.AddComponent<Image>();
            bg.color = Color.black;
            RectTransform bgRect = bgGO.GetComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.sizeDelta = Vector2.zero;
            
            // Loading container
            GameObject containerGO = new GameObject("LoadingContainer");
            containerGO.transform.SetParent(parent.transform, false);
            RectTransform containerRect = containerGO.AddComponent<RectTransform>();
            containerRect.anchorMin = new Vector2(0.5f, 0.5f);
            containerRect.anchorMax = new Vector2(0.5f, 0.5f);
            containerRect.sizeDelta = new Vector2(600, 400);
            containerRect.anchoredPosition = Vector2.zero;
            
            // Loading spinner (animated dot)
            loadingSpinner = CreateLoadingSpinner(containerGO);
            
            // Progress bar
            GameObject progressGO = CreateProgressBar(containerGO);
            loadingProgressBar = progressGO.GetComponentInChildren<Slider>();
            
            // Percentage text
            GameObject percentGO = new GameObject("PercentText");
            percentGO.transform.SetParent(containerGO.transform, false);
            loadingPercentText = percentGO.AddComponent<Text>();
            loadingPercentText.text = "0%";
            loadingPercentText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            loadingPercentText.fontSize = 24;
            loadingPercentText.color = Color.white;
            loadingPercentText.alignment = TextAnchor.MiddleCenter;
            RectTransform percentRect = percentGO.GetComponent<RectTransform>();
            percentRect.anchorMin = new Vector2(0.5f, 0.5f);
            percentRect.anchorMax = new Vector2(0.5f, 0.5f);
            percentRect.sizeDelta = new Vector2(200, 50);
            percentRect.anchoredPosition = new Vector2(0, -50);
            
            // Loading tip text
            GameObject tipGO = new GameObject("TipText");
            tipGO.transform.SetParent(containerGO.transform, false);
            loadingTipText = tipGO.AddComponent<Text>();
            loadingTipText.text = "";
            loadingTipText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            loadingTipText.fontSize = 18;
            loadingTipText.color = new Color(0.8f, 0.8f, 0.8f);
            loadingTipText.alignment = TextAnchor.MiddleCenter;
            RectTransform tipRect = tipGO.GetComponent<RectTransform>();
            tipRect.anchorMin = new Vector2(0.5f, 0.5f);
            tipRect.anchorMax = new Vector2(0.5f, 0.5f);
            tipRect.sizeDelta = new Vector2(500, 60);
            tipRect.anchoredPosition = new Vector2(0, -120);
        }

        private GameObject CreateLoadingSpinner(GameObject parent)
        {
            GameObject spinnerGO = new GameObject("LoadingSpinner");
            spinnerGO.transform.SetParent(parent.transform, false);
            
            // Create dot image (mimics the game's main dot)
            Image dot = spinnerGO.AddComponent<Image>();
            dot.color = Color.white;
            dot.sprite = CreateCircleSprite();
            
            RectTransform spinnerRect = spinnerGO.GetComponent<RectTransform>();
            spinnerRect.anchorMin = new Vector2(0.5f, 0.5f);
            spinnerRect.anchorMax = new Vector2(0.5f, 0.5f);
            spinnerRect.sizeDelta = new Vector2(60, 60);
            spinnerRect.anchoredPosition = new Vector2(0, 50);
            
            // Add rotation animation component
            spinnerGO.AddComponent<LoadingSpinnerAnimation>();
            
            return spinnerGO;
        }

        private GameObject CreateProgressBar(GameObject parent)
        {
            GameObject progressGO = new GameObject("ProgressBar");
            progressGO.transform.SetParent(parent.transform, false);
            
            Slider slider = progressGO.AddComponent<Slider>();
            slider.minValue = 0;
            slider.maxValue = 1;
            slider.value = 0;
            
            // Background
            GameObject bgGO = new GameObject("Background");
            bgGO.transform.SetParent(progressGO.transform, false);
            Image bgImage = bgGO.AddComponent<Image>();
            bgImage.color = new Color(0.2f, 0.2f, 0.2f, 0.8f);
            RectTransform bgRect = bgGO.GetComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.sizeDelta = Vector2.zero;
            
            // Fill area
            GameObject fillAreaGO = new GameObject("Fill Area");
            fillAreaGO.transform.SetParent(progressGO.transform, false);
            RectTransform fillAreaRect = fillAreaGO.AddComponent<RectTransform>();
            fillAreaRect.anchorMin = Vector2.zero;
            fillAreaRect.anchorMax = Vector2.one;
            fillAreaRect.sizeDelta = new Vector2(-10, -10);
            fillAreaRect.anchoredPosition = Vector2.zero;
            
            // Fill
            GameObject fillGO = new GameObject("Fill");
            fillGO.transform.SetParent(fillAreaGO.transform, false);
            Image fillImage = fillGO.AddComponent<Image>();
            fillImage.color = Color.white;
            RectTransform fillRect = fillGO.GetComponent<RectTransform>();
            fillRect.anchorMin = Vector2.zero;
            fillRect.anchorMax = new Vector2(0, 1);
            fillRect.sizeDelta = Vector2.zero;
            fillRect.anchoredPosition = Vector2.zero;
            
            slider.fillRect = fillRect;
            slider.targetGraphic = bgImage;
            
            RectTransform progressRect = progressGO.GetComponent<RectTransform>();
            progressRect.anchorMin = new Vector2(0.5f, 0.5f);
            progressRect.anchorMax = new Vector2(0.5f, 0.5f);
            progressRect.sizeDelta = new Vector2(400, 20);
            progressRect.anchoredPosition = new Vector2(0, 0);
            
            return progressGO;
        }

        private Sprite CreateCircleSprite()
        {
            // Create a simple circle sprite programmatically
            int size = 64;
            Texture2D texture = new Texture2D(size, size);
            Color[] pixels = new Color[size * size];
            
            float radius = size / 2f;
            Vector2 center = new Vector2(radius, radius);
            
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float distance = Vector2.Distance(new Vector2(x, y), center);
                    if (distance <= radius)
                    {
                        float alpha = 1f - (distance / radius) * 0.2f; // Soft edge
                        pixels[y * size + x] = new Color(1, 1, 1, alpha);
                    }
                    else
                    {
                        pixels[y * size + x] = Color.clear;
                    }
                }
            }
            
            texture.SetPixels(pixels);
            texture.Apply();
            
            return Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
        }
        #endregion

        #region Scene Loading Methods
        public void LoadScene(string sceneName, TransitionType? transition = null, Action<bool> callback = null)
        {
            LoadScene(sceneName, new SceneTransitionSettings
            {
                type = transition ?? defaultTransition,
                duration = defaultTransitionDuration,
                curve = transitionCurve,
                color = transitionColor,
                useLoadingScreen = useLoadingScreen
            }, callback);
        }

        public void LoadScene(string sceneName, SceneTransitionSettings settings, Action<bool> callback = null)
        {
            if (isTransitioning || isLoading)
            {
                // Queue the request
                loadQueue.Enqueue(new SceneLoadRequest
                {
                    sceneName = sceneName,
                    transition = settings.type,
                    duration = settings.duration,
                    loadMode = LoadSceneMode.Single,
                    callback = callback,
                    sceneParameters = settings.parameters,
                    priority = 1f
                });
                
                Debug.Log($"[SceneController] Scene load queued: {sceneName}");
                return;
            }
            
            // Check if scene exists
            if (!IsSceneValid(sceneName))
            {
                Debug.LogError($"[SceneController] Scene not found in build settings: {sceneName}");
                OnSceneLoadError?.Invoke($"Scene {sceneName} not found");
                callback?.Invoke(false);
                return;
            }
            
            // Don't reload the same scene unless explicitly allowed
            if (!allowSceneReload && currentSceneName == sceneName)
            {
                Debug.Log($"[SceneController] Already in scene: {sceneName}");
                callback?.Invoke(true);
                return;
            }
            
            StartCoroutine(LoadSceneCoroutine(sceneName, settings, callback));
        }

        public Coroutine LoadSceneAsync(SceneName scene, Action<bool> callback = null)
        {
            return LoadSceneAsync(scene.ToString(), callback);
        }

        public Coroutine LoadSceneAsync(string sceneName, Action<bool> callback = null)
        {
            return StartCoroutine(LoadSceneCoroutine(sceneName, new SceneTransitionSettings
            {
                type = defaultTransition,
                duration = defaultTransitionDuration,
                useLoadingScreen = useLoadingScreen
            }, callback));
        }

        private IEnumerator LoadSceneCoroutine(string sceneName, SceneTransitionSettings settings, Action<bool> callback)
        {
            isLoading = true;
            sceneLoadStartTime = Time.realtimeSinceStartup;
            
            Debug.Log($"[SceneController] Loading scene: {sceneName}");
            
            // Fire events
            previousSceneName = currentSceneName;
            OnSceneLoadStart?.Invoke(sceneName);
            OnSceneTransition?.Invoke(previousSceneName, sceneName);
            
            // Start transition
            if (settings.type != TransitionType.None)
            {
                yield return StartCoroutine(TransitionOut(settings));
            }
            
            // Show loading screen if needed
            bool useLoading = settings.useLoadingScreen && IsLoadingScreenRequired(sceneName);
            if (useLoading)
            {
                ShowLoadingScreen();
                UpdateLoadingTip();
            }
            
            // Start async load
            currentLoadOperation = SceneManager.LoadSceneAsync(sceneName);
            
            if (currentLoadOperation == null)
            {
                Debug.LogError($"[SceneController] Failed to start loading scene: {sceneName}");
                isLoading = false;
                callback?.Invoke(false);
                yield break;
            }
            
            // Configure async operation
            currentLoadOperation.priority = (int)(asyncLoadPriority * 256);
            currentLoadOperation.allowSceneActivation = false;
            
            // Track loading progress
            float elapsedTime = 0;
            float minimumTime = useLoading ? minimumLoadingTime : 0;
            
            while (!currentLoadOperation.isDone)
            {
                elapsedTime += Time.deltaTime;
                float progress = Mathf.Clamp01(currentLoadOperation.progress / 0.9f);
                
                // Update UI
                if (useLoading)
                {
                    UpdateLoadingProgress(progress);
                }
                
                OnLoadProgress?.Invoke(progress);
                
                // Check if ready to activate
                if (currentLoadOperation.progress >= 0.9f)
                {
                    // Ensure minimum loading time
                    if (elapsedTime >= minimumTime)
                    {
                        // Perform cleanup before scene activation
                        if (unloadUnusedAssets)
                        {
                            yield return PerformMemoryCleanup();
                        }
                        
                        // Activate the scene
                        currentLoadOperation.allowSceneActivation = true;
                    }
                }
                
                yield return null;
            }
            
            // Scene is now loaded
            currentSceneName = sceneName;
            
            // Hide loading screen
            if (useLoading)
            {
                HideLoadingScreen();
            }
            
            // Transition in
            if (settings.type != TransitionType.None)
            {
                yield return StartCoroutine(TransitionIn(settings));
            }
            
            // Record load time
            float loadTime = Time.realtimeSinceStartup - sceneLoadStartTime;
            sceneLoadTimes[sceneName] = loadTime;
            
            Debug.Log($"[SceneController] Scene loaded: {sceneName} (Time: {loadTime:F2}s)");
            
            // Fire completion events
            OnSceneLoadComplete?.Invoke(sceneName);
            OnTransitionComplete?.Invoke();
            
            isLoading = false;
            callback?.Invoke(true);
            
            // Process queued loads
            ProcessLoadQueue();
        }

        public void LoadSceneAdditive(string sceneName, Action<bool> callback = null)
        {
            StartCoroutine(LoadSceneAdditiveCoroutine(sceneName, callback));
        }

        private IEnumerator LoadSceneAdditiveCoroutine(string sceneName, Action<bool> callback)
        {
            Debug.Log($"[SceneController] Loading scene additively: {sceneName}");
            
            AsyncOperation loadOp = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Additive);
            
            if (loadOp == null)
            {
                Debug.LogError($"[SceneController] Failed to load scene additively: {sceneName}");
                callback?.Invoke(false);
                yield break;
            }
            
            while (!loadOp.isDone)
            {
                OnLoadProgress?.Invoke(loadOp.progress);
                yield return null;
            }
            
            Debug.Log($"[SceneController] Additive scene loaded: {sceneName}");
            callback?.Invoke(true);
        }

        public void UnloadScene(string sceneName, Action<bool> callback = null)
        {
            StartCoroutine(UnloadSceneCoroutine(sceneName, callback));
        }

        private IEnumerator UnloadSceneCoroutine(string sceneName, Action<bool> callback)
        {
            Debug.Log($"[SceneController] Unloading scene: {sceneName}");
            
            AsyncOperation unloadOp = SceneManager.UnloadSceneAsync(sceneName);
            
            if (unloadOp == null)
            {
                Debug.LogError($"[SceneController] Failed to unload scene: {sceneName}");
                callback?.Invoke(false);
                yield break;
            }
            
            while (!unloadOp.isDone)
            {
                yield return null;
            }
            
            if (unloadUnusedAssets)
            {
                yield return PerformMemoryCleanup();
            }
            
            Debug.Log($"[SceneController] Scene unloaded: {sceneName}");
            callback?.Invoke(true);
        }
        #endregion

        #region Transition Methods
        private IEnumerator TransitionOut(SceneTransitionSettings settings)
        {
            isTransitioning = true;
            transitionCanvas.gameObject.SetActive(true);
            
            float elapsed = 0;
            
            while (elapsed < settings.duration)
            {
                elapsed += Time.deltaTime;
                float progress = elapsed / settings.duration;
                float curveValue = settings.curve.Evaluate(progress);
                
                ApplyTransition(settings.type, curveValue, true);
                currentTransitionProgress = progress;
                
                yield return null;
            }
            
            ApplyTransition(settings.type, 1f, true);
        }

        private IEnumerator TransitionIn(SceneTransitionSettings settings)
        {
            float elapsed = 0;
            
            while (elapsed < settings.duration)
            {
                elapsed += Time.deltaTime;
                float progress = elapsed / settings.duration;
                float curveValue = settings.curve.Evaluate(progress);
                
                ApplyTransition(settings.type, 1f - curveValue, false);
                currentTransitionProgress = progress;
                
                yield return null;
            }
            
            ApplyTransition(settings.type, 0f, false);
            
            transitionCanvas.gameObject.SetActive(false);
            isTransitioning = false;
        }

        private void ApplyTransition(TransitionType type, float progress, bool fadeOut)
        {
            switch (type)
            {
                case TransitionType.Fade:
                    ApplyFadeTransition(progress);
                    break;
                    
                case TransitionType.CircleWipe:
                    ApplyCircleWipeTransition(progress, fadeOut);
                    break;
                    
                case TransitionType.DiamondWipe:
                    ApplyDiamondWipeTransition(progress, fadeOut);
                    break;
                    
                case TransitionType.SlideLeft:
                case TransitionType.SlideRight:
                case TransitionType.SlideUp:
                case TransitionType.SlideDown:
                    ApplySlideTransition(type, progress, fadeOut);
                    break;
                    
                case TransitionType.Dissolve:
                    ApplyDissolveTransition(progress);
                    break;
                    
                case TransitionType.Pixelate:
                    ApplyPixelateTransition(progress, fadeOut);
                    break;
                    
                default:
                    ApplyFadeTransition(progress);
                    break;
            }
        }

        private void ApplyFadeTransition(float alpha)
        {
            if (transitionImage != null)
            {
                Color color = transitionImage.color;
                color.a = alpha;
                transitionImage.color = color;
            }
        }

        private void ApplyCircleWipeTransition(float progress, bool expanding)
        {
            // This would require a custom shader for proper circle wipe
            // For now, using fade as fallback
            ApplyFadeTransition(progress);
        }

        private void ApplyDiamondWipeTransition(float progress, bool expanding)
        {
            // This would require a custom shader for proper diamond wipe
            // For now, using fade as fallback
            ApplyFadeTransition(progress);
        }

        private void ApplySlideTransition(TransitionType type, float progress, bool slideOut)
        {
            if (transitionImage != null)
            {
                RectTransform rect = transitionImage.GetComponent<RectTransform>();
                Vector2 position = Vector2.zero;
                
                float screenWidth = Screen.width;
                float screenHeight = Screen.height;
                
                switch (type)
                {
                    case TransitionType.SlideLeft:
                        position.x = slideOut ? -screenWidth * progress : screenWidth * (1 - progress);
                        break;
                        
                    case TransitionType.SlideRight:
                        position.x = slideOut ? screenWidth * progress : -screenWidth * (1 - progress);
                        break;
                        
                    case TransitionType.SlideUp:
                        position.y = slideOut ? screenHeight * progress : -screenHeight * (1 - progress);
                        break;
                        
                    case TransitionType.SlideDown:
                        position.y = slideOut ? -screenHeight * progress : screenHeight * (1 - progress);
                        break;
                }
                
                rect.anchoredPosition = position;
                
                // Also apply fade for smoother transition
                Color color = transitionImage.color;
                color.a = 1f;
                transitionImage.color = color;
            }
        }

        private void ApplyDissolveTransition(float progress)
        {
            // This would require a dissolve shader
            // For now, using fade as fallback
            ApplyFadeTransition(progress);
        }

        private void ApplyPixelateTransition(float progress, bool pixelating)
        {
            // This would require a pixelation shader
            // For now, using fade as fallback
            ApplyFadeTransition(progress);
        }
        #endregion

        #region Loading Screen Methods
        private void ShowLoadingScreen()
        {
            if (loadingCanvas != null)
            {
                loadingCanvas.gameObject.SetActive(true);
                
                // Reset progress
                if (loadingProgressBar != null)
                {
                    loadingProgressBar.value = 0;
                }
                
                if (loadingPercentText != null)
                {
                    loadingPercentText.text = "0%";
                }
                
                // Start spinner animation
                if (loadingSpinner != null)
                {
                    LoadingSpinnerAnimation spinner = loadingSpinner.GetComponent<LoadingSpinnerAnimation>();
                    if (spinner != null)
                    {
                        spinner.StartSpinning();
                    }
                }
            }
        }

        private void HideLoadingScreen()
        {
            if (loadingCanvas != null)
            {
                // Stop spinner animation
                if (loadingSpinner != null)
                {
                    LoadingSpinnerAnimation spinner = loadingSpinner.GetComponent<LoadingSpinnerAnimation>();
                    if (spinner != null)
                    {
                        spinner.StopSpinning();
                    }
                }
                
                loadingCanvas.gameObject.SetActive(false);
            }
        }

        private void UpdateLoadingProgress(float progress)
        {
            if (showLoadingProgress)
            {
                if (loadingProgressBar != null)
                {
                    loadingProgressBar.value = progress;
                }
                
                if (loadingPercentText != null)
                {
                    int percentage = Mathf.RoundToInt(progress * 100);
                    loadingPercentText.text = $"{percentage}%";
                }
            }
        }

        private void UpdateLoadingTip()
        {
            if (showLoadingTips && loadingTipText != null && loadingTips.Count > 0)
            {
                int randomIndex = UnityEngine.Random.Range(0, loadingTips.Count);
                loadingTipText.text = loadingTips[randomIndex];
            }
        }

        public void AddLoadingTip(string tip)
        {
            if (!string.IsNullOrEmpty(tip) && !loadingTips.Contains(tip))
            {
                loadingTips.Add(tip);
            }
        }

        public void SetLoadingTips(List<string> tips)
        {
            loadingTips = new List<string>(tips);
        }
        #endregion

        #region Scene Management Utilities
        private bool IsSceneValid(string sceneName)
        {
            // Check if scene exists in build settings
            for (int i = 0; i < SceneManager.sceneCountInBuildSettings; i++)
            {
                string scenePath = SceneUtility.GetScenePathByBuildIndex(i);
                string name = System.IO.Path.GetFileNameWithoutExtension(scenePath);
                
                if (name == sceneName)
                {
                    return true;
                }
            }
            
            return false;
        }

        private bool IsLoadingScreenRequired(string sceneName)
        {
            if (sceneDataCache.ContainsKey(sceneName))
            {
                return sceneDataCache[sceneName].requiresLoading;
            }
            
            // Default to showing loading for gameplay scene
            return sceneName == "Gameplay";
        }

        private void ProcessLoadQueue()
        {
            if (loadQueue.Count > 0 && !isLoading && !isTransitioning)
            {
                SceneLoadRequest request = loadQueue.Dequeue();
                
                LoadScene(request.sceneName, new SceneTransitionSettings
                {
                    type = request.transition,
                    duration = request.duration,
                    parameters = request.sceneParameters
                }, request.callback);
            }
        }

        public string GetCurrentSceneName()
        {
            return currentSceneName;
        }

        public string GetPreviousSceneName()
        {
            return previousSceneName;
        }

        public int GetCurrentSceneIndex()
        {
            return SceneManager.GetActiveScene().buildIndex;
        }

        public bool IsSceneLoaded(string sceneName)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.name == sceneName && scene.isLoaded)
                {
                    return true;
                }
            }
            return false;
        }

        public List<string> GetLoadedScenes()
        {
            List<string> loadedScenes = new List<string>();
            
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded)
                {
                    loadedScenes.Add(scene.name);
                }
            }
            
            return loadedScenes;
        }

        public float GetSceneLoadTime(string sceneName)
        {
            if (sceneLoadTimes.ContainsKey(sceneName))
            {
                return sceneLoadTimes[sceneName];
            }
            return -1f;
        }

        public bool IsTransitioning()
        {
            return isTransitioning;
        }

        public bool IsLoading()
        {
            return isLoading;
        }

        public float GetTransitionProgress()
        {
            return currentTransitionProgress;
        }
        #endregion

        #region Memory Management
        private IEnumerator PerformMemoryCleanup()
        {
            Debug.Log("[SceneController] Performing memory cleanup...");
            
            // Unload unused assets
            AsyncOperation unloadOp = Resources.UnloadUnusedAssets();
            
            while (!unloadOp.isDone)
            {
                yield return null;
            }
            
            // Force garbage collection if enabled
            if (forceGarbageCollection)
            {
                System.GC.Collect();
                System.GC.WaitForPendingFinalizers();
                System.GC.Collect();
            }
            
            Debug.Log("[SceneController] Memory cleanup complete");
        }

        private IEnumerator MemoryMonitorRoutine()
        {
            while (true)
            {
                yield return new WaitForSeconds(5f); // Check every 5 seconds
                
                if (Time.time - lastMemoryCheckTime > 10f)
                {
                    CheckMemoryUsage();
                    lastMemoryCheckTime = Time.time;
                }
            }
        }

        private void CheckMemoryUsage()
        {
            #if UNITY_EDITOR || DEVELOPMENT_BUILD
            float memoryUsage = (float)System.GC.GetTotalMemory(false) / (1024 * 1024); // Convert to MB
            float threshold = SystemInfo.systemMemorySize * MEMORY_CLEANUP_THRESHOLD;
            
            if (memoryUsage > threshold)
            {
                Debug.LogWarning($"[SceneController] High memory usage detected: {memoryUsage:F2} MB");
                StartCoroutine(PerformMemoryCleanup());
            }
            #endif
        }
        #endregion

        #region Event Callbacks
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Debug.Log($"[SceneController] Scene loaded callback: {scene.name} (Mode: {mode})");
            
            currentScene = scene;
            
            // Apply scene-specific settings
            ApplySceneSettings(scene.name);
            
            // Update quality settings based on scene
            UpdateQualitySettings(scene.name);
        }

        private void OnSceneUnloaded(Scene scene)
        {
            Debug.Log($"[SceneController] Scene unloaded callback: {scene.name}");
        }

        private void ApplySceneSettings(string sceneName)
        {
            if (sceneDataCache.ContainsKey(sceneName))
            {
                SceneData data = sceneDataCache[sceneName];
                
                // Apply scene-specific settings
                if (data.sceneMusic != null)
                {
                    // AudioManager would play music here
                    Debug.Log($"[SceneController] Would play music for scene: {sceneName}");
                }
            }
        }

        private void UpdateQualitySettings(string sceneName)
        {
            // Adjust quality settings based on scene requirements
            switch (sceneName)
            {
                case "Gameplay":
                    // Optimize for performance during gameplay
                    QualitySettings.vSyncCount = 1;
                    Application.targetFrameRate = 60;
                    break;
                    
                case "Home":
                case "Settings":
                    // Can use higher quality for menus
                    QualitySettings.vSyncCount = 1;
                    Application.targetFrameRate = 30;
                    break;
            }
        }
        #endregion

        #region Public API Methods
        public void LoadHomeScene(Action<bool> callback = null)
        {
            LoadScene("Home", TransitionType.Fade, callback);
        }

        public void LoadGameplayScene(Action<bool> callback = null)
        {
            LoadScene("Gameplay", TransitionType.CircleWipe, callback);
        }

        public void LoadLevelSelectScene(Action<bool> callback = null)
        {
            LoadScene("Levels", TransitionType.SlideLeft, callback);
        }

        public void LoadSettingsScene(Action<bool> callback = null)
        {
            LoadScene("Settings", TransitionType.SlideUp, callback);
        }

        public void ReloadCurrentScene(Action<bool> callback = null)
        {
            if (!string.IsNullOrEmpty(currentSceneName))
            {
                LoadScene(currentSceneName, TransitionType.Fade, callback);
            }
        }

        public void LoadSceneWithCustomTransition(string sceneName, TransitionType transition, 
            float duration, AnimationCurve curve, Action<bool> callback = null)
        {
            LoadScene(sceneName, new SceneTransitionSettings
            {
                type = transition,
                duration = duration,
                curve = curve ?? transitionCurve,
                color = transitionColor,
                useLoadingScreen = useLoadingScreen
            }, callback);
        }

        public void PreloadScene(string sceneName, Action<bool> callback = null)
        {
            StartCoroutine(PreloadSceneCoroutine(sceneName, callback));
        }

        private IEnumerator PreloadSceneCoroutine(string sceneName, Action<bool> callback)
        {
            Debug.Log($"[SceneController] Preloading scene: {sceneName}");
            
            AsyncOperation preloadOp = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Additive);
            preloadOp.allowSceneActivation = false;
            
            while (preloadOp.progress < 0.9f)
            {
                yield return null;
            }
            
            Debug.Log($"[SceneController] Scene preloaded: {sceneName}");
            callback?.Invoke(true);
        }

        public void SetTransitionColor(Color color)
        {
            transitionColor = color;
            if (transitionImage != null)
            {
                Color imageColor = transitionImage.color;
                transitionImage.color = new Color(color.r, color.g, color.b, imageColor.a);
            }
        }

        public void SetTransitionDuration(float duration)
        {
            defaultTransitionDuration = Mathf.Max(0.1f, duration);
        }

        public void SetDefaultTransition(TransitionType transition)
        {
            defaultTransition = transition;
        }
        #endregion

        #region Cleanup
        private void CleanupTransitionComponents()
        {
            if (transitionCanvas != null)
            {
                Destroy(transitionCanvas.gameObject);
            }
            
            if (loadingCanvas != null)
            {
                Destroy(loadingCanvas.gameObject);
            }
            
            if (transitionMaterial != null)
            {
                Destroy(transitionMaterial);
            }
        }
        #endregion

        #region Helper Components
        // Simple spinner animation component
        private class LoadingSpinnerAnimation : MonoBehaviour
        {
            private bool isSpinning;
            private float spinSpeed = 180f; // Degrees per second
            private Coroutine pulseCoroutine;
            
            public void StartSpinning()
            {
                isSpinning = true;
                pulseCoroutine = StartCoroutine(PulseAnimation());
            }
            
            public void StopSpinning()
            {
                isSpinning = false;
                if (pulseCoroutine != null)
                {
                    StopCoroutine(pulseCoroutine);
                }
            }
            
            private void Update()
            {
                if (isSpinning)
                {
                    transform.Rotate(0, 0, spinSpeed * Time.deltaTime);
                }
            }
            
            private IEnumerator PulseAnimation()
            {
                Vector3 originalScale = transform.localScale;
                
                while (isSpinning)
                {
                    // Pulse out
                    float elapsed = 0;
                    while (elapsed < 0.5f)
                    {
                        elapsed += Time.deltaTime;
                        float scale = 1f + (0.2f * Mathf.Sin(elapsed * Mathf.PI / 0.5f));
                        transform.localScale = originalScale * scale;
                        yield return null;
                    }
                    
                    transform.localScale = originalScale;
                    yield return new WaitForSeconds(0.5f);
                }
            }
        }
        #endregion

        #region Debug Methods
        #if UNITY_EDITOR
        [UnityEditor.MenuItem("Just a Dot/Scene Controller/Log Scene Info")]
        private static void LogSceneInfo()
        {
            Debug.Log($"[SceneController] Active Scene: {SceneManager.GetActiveScene().name}");
            Debug.Log($"[SceneController] Loaded Scene Count: {SceneManager.sceneCount}");
            Debug.Log($"[SceneController] Build Scene Count: {SceneManager.sceneCountInBuildSettings}");
            
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                Debug.Log($"  - {scene.name} (Loaded: {scene.isLoaded})");
            }
        }
        
        [UnityEditor.MenuItem("Just a Dot/Scene Controller/Force Memory Cleanup")]
        private static void ForceMemoryCleanup()
        {
            Resources.UnloadUnusedAssets();
            System.GC.Collect();
            Debug.Log("[SceneController] Forced memory cleanup complete");
        }
        #endif
        
        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        public void LogPerformanceMetrics()
        {
            Debug.Log("[SceneController] Performance Metrics:");
            Debug.Log($"  - Current Scene: {currentSceneName}");
            Debug.Log($"  - Is Loading: {isLoading}");
            Debug.Log($"  - Is Transitioning: {isTransitioning}");
            Debug.Log($"  - Queue Size: {loadQueue.Count}");
            Debug.Log($"  - Memory Usage: {System.GC.GetTotalMemory(false) / (1024 * 1024):F2} MB");
            
            foreach (var kvp in sceneLoadTimes)
            {
                Debug.Log($"  - {kvp.Key} Load Time: {kvp.Value:F2}s");
            }
        }
        #endregion
    }
}