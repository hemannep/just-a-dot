using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Events;

namespace JustADot.Core
{
    /// <summary>
    /// Master Game Controller - Singleton pattern implementation
    /// Manages game state, progression, and coordinates all major systems
    /// </summary>
    public class GameManager : MonoBehaviour
    {
        #region Singleton Pattern
        private static GameManager _instance;
        public static GameManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = FindFirstObjectByType<GameManager>();
                    if (_instance == null)
                    {
                        GameObject go = new GameObject("GameManager");
                        _instance = go.AddComponent<GameManager>();
                        DontDestroyOnLoad(go);
                    }
                }
                return _instance;
            }
        }
        #endregion

        #region Enums & Constants
        public enum GameState
        {
            Initializing,
            MainMenu,
            LevelSelect,
            Playing,
            Paused,
            LevelComplete,
            GameComplete,
            Settings,
            Loading
        }

        public enum SceneIndex
        {
            Splash = 0,
            Home = 1,
            Levels = 2,
            Gameplay = 3,
            Settings = 4
        }

        private const string SAVE_KEY = "JustADot_SaveData";
        private const string SETTINGS_KEY = "JustADot_Settings";
        private const string FIRST_LAUNCH_KEY = "JustADot_FirstLaunch";
        private const float AUTO_SAVE_INTERVAL = 30f; // Auto-save every 30 seconds
        private const int MAX_LEVELS = 100;
        #endregion

        #region Public Properties
        public GameState CurrentGameState { get; private set; }
        public int CurrentLevel { get; private set; }
        public int HighestUnlockedLevel { get; private set; }
        public bool IsPremium { get; private set; }
        public bool AdsRemoved { get; private set; }
        public float TotalPlayTime { get; private set; }
        public DateTime FirstLaunchDate { get; private set; }
        public bool IsFirstLaunch { get; private set; }
        #endregion

        #region Events
        public static UnityEvent<GameState> OnGameStateChanged = new UnityEvent<GameState>();
        public static UnityEvent<int> OnLevelCompleted = new UnityEvent<int>();
        public static UnityEvent<int> OnLevelStarted = new UnityEvent<int>();
        public static UnityEvent OnGameInitialized = new UnityEvent();
        public static UnityEvent<float> OnProgressUpdated = new UnityEvent<float>();
        public static UnityEvent<string> OnAchievementUnlocked = new UnityEvent<string>();
        #endregion

        #region Private Variables
        // System References (These are placeholder classes that need to be created)
        private SaveSystem saveSystem;
        private SceneController sceneController;
        
        // Manager stubs - these will be found in their respective scenes
        private MonoBehaviour levelManager;
        private MonoBehaviour uiManager;
        private MonoBehaviour audioManager;
        private MonoBehaviour sensorManager;
        private MonoBehaviour adManager;
        private MonoBehaviour analyticsManager;

        // Game Data
        private SaveData currentSaveData;
        private GameSettings gameSettings;
        private LevelData[] allLevels;
        private Dictionary<int, LevelProgress> levelProgress;
        private List<string> unlockedAchievements;

        // State Management
        private GameState previousState;
        private bool isTransitioning;
        private Coroutine autoSaveCoroutine;
        private float sessionStartTime;

        // Performance
        private float lastFrameTime;
        private int frameCount;
        private float fps;
        #endregion

        #region Data Classes
        [Serializable]
        public class SaveData
        {
            public int highestUnlockedLevel = 1;
            public int currentLevel = 1;
            public Dictionary<int, LevelProgress> levelProgress = new Dictionary<int, LevelProgress>();
            public List<string> unlockedAchievements = new List<string>();
            public float totalPlayTime = 0f;
            public bool isPremium = false;
            public bool adsRemoved = false;
            public string firstLaunchDate = "";
            public int totalAttempts = 0;
            public int totalCompletions = 0;
            public Dictionary<string, int> statistics = new Dictionary<string, int>();
        }

        [Serializable]
        public class LevelProgress
        {
            public bool isCompleted;
            public float bestTime;
            public int attempts;
            public DateTime firstCompletionDate;
            public bool perfectCompletion;
            public bool hintUsed;
        }

        [Serializable]
        public class GameSettings
        {
            public bool soundEnabled = true;
            public bool musicEnabled = true;
            public bool vibrationEnabled = true;
            public float masterVolume = 1f;
            public string language = "en";
            public bool notificationsEnabled = false;
            public bool highContrastMode = false;
            public float dotSize = 1f;
        }

        [Serializable]
        public class LevelData
        {
            public int id;
            public string name;
            public string hint;
            public string theme;
            public string[] sensors;
            public string solution;
            public string successAnimation;
            public int difficulty;
        }
        #endregion

        #region Unity Lifecycle
        private void Awake()
        {
            // Singleton enforcement
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);

            InitializeGameManager();
        }

        private void Start()
        {
            StartCoroutine(DelayedStart());
        }

        private IEnumerator DelayedStart()
        {
            yield return new WaitForSeconds(0.1f);
            
            // Check first launch
            CheckFirstLaunch();
            
            // Start session
            sessionStartTime = Time.time;
            
            // Begin auto-save
            autoSaveCoroutine = StartCoroutine(AutoSaveRoutine());
            
            // Notify initialization complete
            OnGameInitialized?.Invoke();
            
            Debug.Log("[GameManager] Initialization complete");
        }

        private void Update()
        {
            // Update play time
            if (CurrentGameState == GameState.Playing)
            {
                TotalPlayTime += Time.deltaTime;
            }

            // Calculate FPS (for performance monitoring)
            CalculateFPS();

            // Handle back button (Android)
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                HandleBackButton();
            }
        }

        private void OnApplicationPause(bool pauseStatus)
        {
            if (pauseStatus)
            {
                SaveGame();
                if (CurrentGameState == GameState.Playing)
                {
                    PauseGame();
                }
            }
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus && CurrentGameState == GameState.Playing)
            {
                SaveGame();
            }
        }

        private void OnApplicationQuit()
        {
            SaveGame();
            SaveSettings();
        }

        private void OnDestroy()
        {
            if (autoSaveCoroutine != null)
            {
                StopCoroutine(autoSaveCoroutine);
            }
        }
        #endregion

        #region Initialization
        private void InitializeGameManager()
        {
            CurrentGameState = GameState.Initializing;
            
            // Initialize data structures
            levelProgress = new Dictionary<int, LevelProgress>();
            unlockedAchievements = new List<string>();
            
            // Load saved data
            LoadGame();
            LoadSettings();
            
            // Initialize subsystems
            InitializeSubsystems();
            
            // Load level data
            LoadLevelData();
        }

        private void InitializeSubsystems()
        {
            // Find or create subsystem managers
            saveSystem = GetOrCreateComponent<SaveSystem>();
            sceneController = GetOrCreateComponent<SceneController>();
            
            // These will be found in their respective scenes
            StartCoroutine(FindSubsystemsDelayed());
        }

        private IEnumerator FindSubsystemsDelayed()
        {
            yield return new WaitForSeconds(0.5f);
            
            // Find managers as MonoBehaviours - actual manager scripts will inherit from these
            levelManager = FindFirstObjectByType<MonoBehaviour>();
            uiManager = FindFirstObjectByType<MonoBehaviour>();
            audioManager = FindFirstObjectByType<MonoBehaviour>();
            sensorManager = FindFirstObjectByType<MonoBehaviour>();
            adManager = FindFirstObjectByType<MonoBehaviour>();
            analyticsManager = FindFirstObjectByType<MonoBehaviour>();
        }

        private T GetOrCreateComponent<T>() where T : Component
        {
            T component = GetComponent<T>();
            if (component == null)
            {
                component = gameObject.AddComponent<T>();
            }
            return component;
        }

        private void LoadLevelData()
        {
            // Load from Resources or JSON file
            TextAsset levelJson = Resources.Load<TextAsset>("Data/levels");
            if (levelJson != null)
            {
                try
                {
                    LevelDataWrapper wrapper = JsonUtility.FromJson<LevelDataWrapper>(levelJson.text);
                    allLevels = wrapper.levels;
                    Debug.Log($"[GameManager] Loaded {allLevels.Length} levels");
                }
                catch (Exception e)
                {
                    Debug.LogError($"[GameManager] Failed to load level data: {e.Message}");
                    CreateDefaultLevels();
                }
            }
            else
            {
                Debug.LogWarning("[GameManager] Level data not found, creating defaults");
                CreateDefaultLevels();
            }
        }

        private void CreateDefaultLevels()
        {
            // Create default level structure if JSON not found
            allLevels = new LevelData[MAX_LEVELS];
            for (int i = 0; i < MAX_LEVELS; i++)
            {
                allLevels[i] = new LevelData
                {
                    id = i + 1,
                    name = $"Level {i + 1}",
                    hint = "Discover",
                    theme = GetThemeForLevel(i + 1),
                    sensors = new string[] { },
                    solution = "tap",
                    successAnimation = "pulse",
                    difficulty = (i / 10) + 1
                };
            }
        }

        private string GetThemeForLevel(int level)
        {
            int themeIndex = (level - 1) / 10;
            string[] themes = {
                "Touch & Gestures",
                "Motion & Orientation", 
                "Device Hardware",
                "Time & Temporal",
                "Audio & Voice",
                "Camera & Visual",
                "Location & Movement",
                "Combinations & Advanced",
                "Meta & Social",
                "Mastery & Transcendence"
            };
            return themeIndex < themes.Length ? themes[themeIndex] : "Unknown";
        }

        private void CheckFirstLaunch()
        {
            string firstLaunchDate = PlayerPrefs.GetString(FIRST_LAUNCH_KEY, "");
            if (string.IsNullOrEmpty(firstLaunchDate))
            {
                IsFirstLaunch = true;
                FirstLaunchDate = DateTime.Now;
                PlayerPrefs.SetString(FIRST_LAUNCH_KEY, FirstLaunchDate.ToString());
                PlayerPrefs.Save();
                
                // Track first launch (simplified - actual implementation would use real analytics)
                Debug.Log($"[GameManager] First launch detected at {FirstLaunchDate}");
            }
            else
            {
                IsFirstLaunch = false;
                if (!DateTime.TryParse(firstLaunchDate, out DateTime parsedDate))
                {
                    FirstLaunchDate = DateTime.Now;
                }
                else
                {
                    FirstLaunchDate = parsedDate;
                }
            }
        }
        #endregion

        #region State Management
        public void ChangeGameState(GameState newState)
        {
            if (isTransitioning) return;
            
            previousState = CurrentGameState;
            CurrentGameState = newState;
            
            Debug.Log($"[GameManager] State changed: {previousState} -> {newState}");
            
            OnGameStateChanged?.Invoke(newState);
            
            // Handle state-specific logic
            HandleStateChange(newState);
        }

        private void HandleStateChange(GameState newState)
        {
            switch (newState)
            {
                case GameState.MainMenu:
                    Time.timeScale = 1f;
                    // audioManager?.PlayMenuMusic();
                    Debug.Log("[GameManager] Menu music would play here");
                    break;
                    
                case GameState.Playing:
                    Time.timeScale = 1f;
                    // audioManager?.PlayGameplayMusic(CurrentLevel);
                    Debug.Log($"[GameManager] Gameplay music for level {CurrentLevel} would play here");
                    break;
                    
                case GameState.Paused:
                    Time.timeScale = 0f;
                    SaveGame();
                    break;
                    
                case GameState.LevelComplete:
                    HandleLevelCompletion();
                    break;
                    
                case GameState.GameComplete:
                    HandleGameCompletion();
                    break;
            }
        }

        public void ReturnToPreviousState()
        {
            if (previousState != CurrentGameState)
            {
                ChangeGameState(previousState);
            }
        }
        #endregion

        #region Level Management
        public void StartLevel(int levelNumber)
        {
            if (levelNumber < 1 || levelNumber > MAX_LEVELS)
            {
                Debug.LogError($"[GameManager] Invalid level number: {levelNumber}");
                return;
            }

            if (levelNumber > HighestUnlockedLevel)
            {
                Debug.LogWarning($"[GameManager] Level {levelNumber} is locked");
                // uiManager?.ShowMessage("Level Locked", "Complete previous levels first!");
                Debug.Log("UI Message: Level Locked - Complete previous levels first!");
                return;
            }

            StartCoroutine(LoadLevelAsync(levelNumber));
        }

        private IEnumerator LoadLevelAsync(int levelNumber)
        {
            ChangeGameState(GameState.Loading);
            
            // Show loading UI (placeholder)
            Debug.Log("[GameManager] Showing loading screen");
            
            // Load gameplay scene if not already loaded
            if (SceneManager.GetActiveScene().buildIndex != (int)SceneIndex.Gameplay)
            {
                if (sceneController != null)
                {
                    bool loadComplete = false;
                    sceneController.LoadSceneAsync("Gameplay", (success) => { loadComplete = success; });
                    
                    while (!loadComplete)
                    {
                        yield return null;
                    }
                }
                else
                {
                    yield return SceneManager.LoadSceneAsync((int)SceneIndex.Gameplay);
                }
            }
            
            // Wait for level manager
            while (levelManager == null)
            {
                levelManager = FindFirstObjectByType<MonoBehaviour>();
                yield return null;
            }
            
            // Initialize level
            CurrentLevel = levelNumber;
            LevelData levelData = GetLevelData(levelNumber);
            
            if (levelData != null)
            {
                // levelManager.InitializeLevel(levelData);
                Debug.Log($"[GameManager] Level {levelNumber} initialized with data: {levelData.name}");
                
                // Track level start
                TrackLevelStart(levelNumber);
                
                // Update UI
                Debug.Log($"[GameManager] UI updated for level: {levelData.name}");
                
                // Start gameplay
                ChangeGameState(GameState.Playing);
                OnLevelStarted?.Invoke(levelNumber);
            }
            else
            {
                Debug.LogError($"[GameManager] Level data not found for level {levelNumber}");
            }
            
            Debug.Log("[GameManager] Hiding loading screen");
        }

        public void CompleteCurrentLevel(float completionTime, bool perfectCompletion = false)
        {
            if (CurrentGameState != GameState.Playing) return;
            
            // Update level progress
            UpdateLevelProgress(CurrentLevel, completionTime, perfectCompletion);
            
            // Unlock next level
            if (CurrentLevel >= HighestUnlockedLevel && CurrentLevel < MAX_LEVELS)
            {
                HighestUnlockedLevel = CurrentLevel + 1;
            }
            
            // Save progress
            SaveGame();
            
            // Track completion
            TrackLevelCompletion(CurrentLevel, completionTime, perfectCompletion);
            
            // Fire event
            OnLevelCompleted?.Invoke(CurrentLevel);
            
            // Change state
            ChangeGameState(GameState.LevelComplete);
        }

        private void UpdateLevelProgress(int level, float completionTime, bool perfect)
        {
            if (!levelProgress.ContainsKey(level))
            {
                levelProgress[level] = new LevelProgress();
            }
            
            LevelProgress progress = levelProgress[level];
            progress.isCompleted = true;
            progress.attempts++;
            
            if (!progress.isCompleted || completionTime < progress.bestTime)
            {
                progress.bestTime = completionTime;
            }
            
            if (!progress.isCompleted)
            {
                progress.firstCompletionDate = DateTime.Now;
            }
            
            if (perfect)
            {
                progress.perfectCompletion = true;
            }
            
            // Check for achievements
            CheckAchievements();
        }

        private void HandleLevelCompletion()
        {
            // Show completion UI (placeholder)
            Debug.Log($"[GameManager] Level {CurrentLevel} complete! Showing completion UI");
            
            // Play success sound (placeholder)
            Debug.Log("[GameManager] Playing level complete sound");
            
            // Auto-proceed after delay (optional)
            if (CurrentLevel < MAX_LEVELS)
            {
                StartCoroutine(AutoProceedToNextLevel());
            }
            else
            {
                ChangeGameState(GameState.GameComplete);
            }
        }

        private IEnumerator AutoProceedToNextLevel()
        {
            yield return new WaitForSeconds(3f);
            
            if (CurrentGameState == GameState.LevelComplete)
            {
                NextLevel();
            }
        }

        public void NextLevel()
        {
            if (CurrentLevel < MAX_LEVELS)
            {
                StartLevel(CurrentLevel + 1);
            }
            else
            {
                ChangeGameState(GameState.GameComplete);
            }
        }

        public void RestartLevel()
        {
            StartLevel(CurrentLevel);
        }

        public void ReturnToMenu()
        {
            StartCoroutine(ReturnToMenuAsync());
        }

        private IEnumerator ReturnToMenuAsync()
        {
            ChangeGameState(GameState.Loading);
            SaveGame();
            
            if (sceneController != null)
            {
                bool loadComplete = false;
                sceneController.LoadSceneAsync("Home", (success) => { loadComplete = success; });
                
                while (!loadComplete)
                {
                    yield return null;
                }
            }
            else
            {
                yield return SceneManager.LoadSceneAsync((int)SceneIndex.Home);
            }
            
            ChangeGameState(GameState.MainMenu);
        }

        public LevelData GetLevelData(int levelNumber)
        {
            if (allLevels != null && levelNumber > 0 && levelNumber <= allLevels.Length)
            {
                return allLevels[levelNumber - 1];
            }
            return null;
        }

        private void HandleGameCompletion()
        {
            // Special handling for completing all 100 levels
            Debug.Log("[GameManager] Game completed! All 100 levels finished!");
            
            // Unlock special achievement
            UnlockAchievement("master_of_dots");
            
            // Show game complete screen (placeholder)
            Debug.Log("[GameManager] Showing game complete screen");
            
            // Track completion (simplified analytics)
            Debug.Log($"[GameManager] Game completed - Total time: {TotalPlayTime}, Total attempts: {currentSaveData?.totalAttempts ?? 0}");
        }
        #endregion

        #region Save/Load System
        public void SaveGame()
        {
            try
            {
                if (currentSaveData == null)
                {
                    currentSaveData = new SaveData();
                }
                
                // Update save data
                currentSaveData.highestUnlockedLevel = HighestUnlockedLevel;
                currentSaveData.currentLevel = CurrentLevel;
                currentSaveData.levelProgress = levelProgress;
                currentSaveData.unlockedAchievements = unlockedAchievements;
                currentSaveData.totalPlayTime = TotalPlayTime;
                currentSaveData.isPremium = IsPremium;
                currentSaveData.adsRemoved = AdsRemoved;
                
                // Serialize and save
                string jsonData = JsonUtility.ToJson(currentSaveData, true);
                PlayerPrefs.SetString(SAVE_KEY, jsonData);
                PlayerPrefs.Save();
                
                Debug.Log("[GameManager] Game saved successfully");
            }
            catch (Exception e)
            {
                Debug.LogError($"[GameManager] Save failed: {e.Message}");
            }
        }

        public void LoadGame()
        {
            try
            {
                string jsonData = PlayerPrefs.GetString(SAVE_KEY, "");
                
                if (!string.IsNullOrEmpty(jsonData))
                {
                    currentSaveData = JsonUtility.FromJson<SaveData>(jsonData);
                    
                    // Apply loaded data
                    HighestUnlockedLevel = currentSaveData.highestUnlockedLevel;
                    CurrentLevel = currentSaveData.currentLevel;
                    levelProgress = currentSaveData.levelProgress ?? new Dictionary<int, LevelProgress>();
                    unlockedAchievements = currentSaveData.unlockedAchievements ?? new List<string>();
                    TotalPlayTime = currentSaveData.totalPlayTime;
                    IsPremium = currentSaveData.isPremium;
                    AdsRemoved = currentSaveData.adsRemoved;
                    
                    Debug.Log($"[GameManager] Game loaded: Level {CurrentLevel}/{HighestUnlockedLevel}");
                }
                else
                {
                    // Create new save data
                    CreateNewSave();
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[GameManager] Load failed: {e.Message}");
                CreateNewSave();
            }
        }

        private void CreateNewSave()
        {
            currentSaveData = new SaveData();
            HighestUnlockedLevel = 1;
            CurrentLevel = 1;
            levelProgress = new Dictionary<int, LevelProgress>();
            unlockedAchievements = new List<string>();
            TotalPlayTime = 0f;
            IsPremium = false;
            AdsRemoved = false;
            
            SaveGame();
            Debug.Log("[GameManager] New save created");
        }

        public void SaveSettings()
        {
            try
            {
                string jsonData = JsonUtility.ToJson(gameSettings, true);
                PlayerPrefs.SetString(SETTINGS_KEY, jsonData);
                PlayerPrefs.Save();
            }
            catch (Exception e)
            {
                Debug.LogError($"[GameManager] Settings save failed: {e.Message}");
            }
        }

        public void LoadSettings()
        {
            try
            {
                string jsonData = PlayerPrefs.GetString(SETTINGS_KEY, "");
                
                if (!string.IsNullOrEmpty(jsonData))
                {
                    gameSettings = JsonUtility.FromJson<GameSettings>(jsonData);
                }
                else
                {
                    gameSettings = new GameSettings();
                    SaveSettings();
                }
                
                ApplySettings();
            }
            catch (Exception e)
            {
                Debug.LogError($"[GameManager] Settings load failed: {e.Message}");
                gameSettings = new GameSettings();
            }
        }

        private void ApplySettings()
        {
            // Apply loaded settings to game systems (simplified)
            if (audioManager != null)
            {
                // audioManager.SetSoundEnabled(gameSettings.soundEnabled);
                // audioManager.SetMusicEnabled(gameSettings.musicEnabled);
                // audioManager.SetMasterVolume(gameSettings.masterVolume);
                Debug.Log($"[GameManager] Audio settings applied - Sound: {gameSettings.soundEnabled}, Music: {gameSettings.musicEnabled}");
            }
            
            // Apply other settings as needed
            Vibration.SetEnabled(gameSettings.vibrationEnabled);
        }

        private IEnumerator AutoSaveRoutine()
        {
            while (true)
            {
                yield return new WaitForSeconds(AUTO_SAVE_INTERVAL);
                
                if (CurrentGameState == GameState.Playing)
                {
                    SaveGame();
                    Debug.Log("[GameManager] Auto-save completed");
                }
            }
        }

        public void ResetProgress()
        {
            // Simplified confirm dialog
            Debug.Log("[GameManager] Reset progress requested - would show confirmation dialog");
            
            // For now, just reset directly
            CreateNewSave();
            ReturnToMenu();
            Debug.Log("[GameManager] Progress reset complete");
        }
        #endregion

        #region Achievements
        private void CheckAchievements()
        {
            // Check various achievement conditions
            CheckCompletionAchievements();
            CheckSpeedAchievements();
            CheckPerfectionAchievements();
            CheckThemeAchievements();
        }

        private void CheckCompletionAchievements()
        {
            int completedLevels = 0;
            foreach (var progress in levelProgress.Values)
            {
                if (progress.isCompleted) completedLevels++;
            }
            
            // Milestone achievements
            if (completedLevels >= 10) UnlockAchievement("first_ten");
            if (completedLevels >= 25) UnlockAchievement("quarter_way");
            if (completedLevels >= 50) UnlockAchievement("halfway_there");
            if (completedLevels >= 75) UnlockAchievement("almost_done");
            if (completedLevels >= 100) UnlockAchievement("completionist");
        }

        private void CheckSpeedAchievements()
        {
            // Check for speed run achievements
            if (levelProgress.ContainsKey(CurrentLevel))
            {
                float time = levelProgress[CurrentLevel].bestTime;
                if (time < 1f) UnlockAchievement("lightning_fast");
                if (time < 0.5f) UnlockAchievement("instant_genius");
            }
        }

        private void CheckPerfectionAchievements()
        {
            int perfectLevels = 0;
            foreach (var progress in levelProgress.Values)
            {
                if (progress.perfectCompletion) perfectLevels++;
            }
            
            if (perfectLevels >= 10) UnlockAchievement("perfectionist");
            if (perfectLevels >= 50) UnlockAchievement("flawless_execution");
        }

        private void CheckThemeAchievements()
        {
            // Check if player completed all levels in a theme
            for (int theme = 0; theme < 10; theme++)
            {
                bool themeComplete = true;
                for (int level = theme * 10 + 1; level <= (theme + 1) * 10; level++)
                {
                    if (!levelProgress.ContainsKey(level) || !levelProgress[level].isCompleted)
                    {
                        themeComplete = false;
                        break;
                    }
                }
                
                if (themeComplete)
                {
                    UnlockAchievement($"theme_{theme}_master");
                }
            }
        }

        public void UnlockAchievement(string achievementId)
        {
            if (!unlockedAchievements.Contains(achievementId))
            {
                unlockedAchievements.Add(achievementId);
                OnAchievementUnlocked?.Invoke(achievementId);
                
                // Show notification (placeholder)
                Debug.Log($"[GameManager] Achievement unlocked: {achievementId}");
                
                // Play sound (placeholder)
                Debug.Log("[GameManager] Playing achievement sound");
                
                // Track analytics (simplified)
                Debug.Log($"[GameManager] Tracking achievement: {achievementId}");
                
                // Save progress
                SaveGame();
            }
        }
        #endregion

        #region Monetization
        public void PurchaseRemoveAds()
        {
            // Simplified ad purchase (placeholder)
            Debug.Log("[GameManager] Purchase remove ads requested");
            
            // For now, just mark as purchased
            AdsRemoved = true;
            SaveGame();
            Debug.Log("[GameManager] Ads removed successfully");
        }

        public void RestorePurchases()
        {
            // Simplified restore purchases (placeholder)
            Debug.Log("[GameManager] Restore purchases requested");
            
            // Check if purchases should be restored
            if (AdsRemoved)
            {
                Debug.Log("[GameManager] Purchases already restored");
            }
            else
            {
                Debug.Log("[GameManager] No purchases to restore");
            }
        }

        public void ShowRewardedAd(Action<bool> callback)
        {
            if (!AdsRemoved)
            {
                // Simplified rewarded ad (placeholder)
                Debug.Log("[GameManager] Showing rewarded ad");
                
                // Simulate ad watched after delay
                StartCoroutine(SimulateRewardedAd(callback));
            }
            else
            {
                callback?.Invoke(true); // If ads removed, always give reward
            }
        }
        
        private IEnumerator SimulateRewardedAd(Action<bool> callback)
        {
            yield return new WaitForSeconds(1f); // Simulate ad duration
            Debug.Log("[GameManager] Rewarded ad completed");
            callback?.Invoke(true);
        }
        #endregion

        #region Analytics & Tracking
        private void TrackLevelStart(int level)
        {
            // Simplified analytics tracking
            Debug.Log($"[GameManager] Tracking level start: Level {level}, Theme: {GetThemeForLevel(level)}");
            
            if (currentSaveData != null)
            {
                currentSaveData.totalAttempts++;
            }
        }

        private void TrackLevelCompletion(int level, float time, bool perfect)
        {
            // Simplified analytics tracking
            Debug.Log($"[GameManager] Tracking level completion: Level {level}, Time: {time:F2}s, Perfect: {perfect}");
            
            // Update statistics
            if (currentSaveData != null)
            {
                currentSaveData.totalCompletions++;
            }
        }

        public float GetGameProgress()
        {
            int completedLevels = 0;
            foreach (var prog in levelProgress.Values)
            {
                if (prog.isCompleted) completedLevels++;
            }
            
            float gameProgress = (float)completedLevels / MAX_LEVELS;
            OnProgressUpdated?.Invoke(gameProgress);
            return gameProgress;
        }
        #endregion

        #region Settings Management
        public void SetSoundEnabled(bool enabled)
        {
            gameSettings.soundEnabled = enabled;
            // Simplified audio manager interaction
            Debug.Log($"[GameManager] Sound enabled: {enabled}");
            SaveSettings();
        }

        public void SetMusicEnabled(bool enabled)
        {
            gameSettings.musicEnabled = enabled;
            // Simplified audio manager interaction
            Debug.Log($"[GameManager] Music enabled: {enabled}");
            SaveSettings();
        }

        public void SetVibrationEnabled(bool enabled)
        {
            gameSettings.vibrationEnabled = enabled;
            Vibration.SetEnabled(enabled);
            SaveSettings();
        }

        public void SetMasterVolume(float volume)
        {
            gameSettings.masterVolume = Mathf.Clamp01(volume);
            // Simplified audio manager interaction
            Debug.Log($"[GameManager] Master volume set to: {gameSettings.masterVolume}");
            SaveSettings();
        }

        public void SetLanguage(string languageCode)
        {
            gameSettings.language = languageCode;
            // Apply language change to UI (simplified)
            Debug.Log($"[GameManager] Language set to: {languageCode}");
            SaveSettings();
        }

        public void SetNotificationsEnabled(bool enabled)
        {
            gameSettings.notificationsEnabled = enabled;
            // Request notification permission if enabling
            if (enabled)
            {
                NotificationManager.RequestPermission();
            }
            SaveSettings();
        }

        public void SetHighContrastMode(bool enabled)
        {
            gameSettings.highContrastMode = enabled;
            // Apply visual changes (simplified)
            Debug.Log($"[GameManager] High contrast mode: {enabled}");
            SaveSettings();
        }

        public void SetDotSize(float size)
        {
            gameSettings.dotSize = Mathf.Clamp(size, 0.5f, 2f);
            // Apply to dot controller if in gameplay (simplified)
            if (CurrentGameState == GameState.Playing)
            {
                Debug.Log($"[GameManager] Dot size set to: {gameSettings.dotSize}");
            }
            SaveSettings();
        }

        public GameSettings GetSettings()
        {
            return gameSettings;
        }
        #endregion

        #region Helper Methods
        private void CalculateFPS()
        {
            frameCount++;
            if (Time.realtimeSinceStartup - lastFrameTime > 1f)
            {
                fps = frameCount / (Time.realtimeSinceStartup - lastFrameTime);
                frameCount = 0;
                lastFrameTime = Time.realtimeSinceStartup;
            }
        }

        public float GetFPS()
        {
            return fps;
        }

        private void HandleBackButton()
        {
            switch (CurrentGameState)
            {
                case GameState.Playing:
                    PauseGame();
                    break;
                    
                case GameState.Paused:
                    ResumeGame();
                    break;
                    
                case GameState.LevelSelect:
                case GameState.Settings:
                    ReturnToMenu();
                    break;
                    
                case GameState.MainMenu:
                    // Show exit confirmation (simplified)
                    Debug.Log("[GameManager] Exit game requested - would show confirmation dialog");
                    // For now, just quit
                    #if UNITY_EDITOR
                    UnityEditor.EditorApplication.isPlaying = false;
                    #else
                    Application.Quit();
                    #endif
                    break;
            }
        }

        public void PauseGame()
        {
            if (CurrentGameState == GameState.Playing)
            {
                ChangeGameState(GameState.Paused);
                // Show pause menu (simplified)
                Debug.Log("[GameManager] Game paused - showing pause menu");
            }
        }

        public void ResumeGame()
        {
            if (CurrentGameState == GameState.Paused)
            {
                ChangeGameState(GameState.Playing);
                // Hide pause menu (simplified)
                Debug.Log("[GameManager] Game resumed - hiding pause menu");
            }
        }

        public bool IsLevelUnlocked(int levelNumber)
        {
            return levelNumber <= HighestUnlockedLevel;
        }

        public LevelProgress GetLevelProgress(int levelNumber)
        {
            if (levelProgress.ContainsKey(levelNumber))
            {
                return levelProgress[levelNumber];
            }
            return null;
        }

        public List<LevelData> GetAllLevels()
        {
            return new List<LevelData>(allLevels);
        }

        public int GetCompletedLevelCount()
        {
            int count = 0;
            foreach (var progress in levelProgress.Values)
            {
                if (progress.isCompleted) count++;
            }
            return count;
        }

        public float GetTotalBestTime()
        {
            float totalTime = 0f;
            foreach (var progress in levelProgress.Values)
            {
                if (progress.isCompleted)
                {
                    totalTime += progress.bestTime;
                }
            }
            return totalTime;
        }

        public Dictionary<string, int> GetStatistics()
        {
            if (currentSaveData != null && currentSaveData.statistics != null)
            {
                return new Dictionary<string, int>(currentSaveData.statistics);
            }
            return new Dictionary<string, int>();
        }

        public void IncrementStatistic(string key, int amount = 1)
        {
            if (currentSaveData != null)
            {
                if (currentSaveData.statistics == null)
                {
                    currentSaveData.statistics = new Dictionary<string, int>();
                }
                
                if (!currentSaveData.statistics.ContainsKey(key))
                {
                    currentSaveData.statistics[key] = 0;
                }
                
                currentSaveData.statistics[key] += amount;
            }
        }
        #endregion

        #region Debug Methods
        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        public void DebugUnlockAllLevels()
        {
            HighestUnlockedLevel = MAX_LEVELS;
            SaveGame();
            Debug.Log("[GameManager] All levels unlocked (DEBUG)");
        }

        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        public void DebugCompleteLevel(int level)
        {
            if (!levelProgress.ContainsKey(level))
            {
                levelProgress[level] = new LevelProgress();
            }
            
            levelProgress[level].isCompleted = true;
            levelProgress[level].bestTime = UnityEngine.Random.Range(1f, 10f);
            levelProgress[level].attempts = 1;
            levelProgress[level].firstCompletionDate = DateTime.Now;
            
            if (level >= HighestUnlockedLevel && level < MAX_LEVELS)
            {
                HighestUnlockedLevel = level + 1;
            }
            
            SaveGame();
            Debug.Log($"[GameManager] Level {level} completed (DEBUG)");
        }

        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        public void DebugResetAllProgress()
        {
            PlayerPrefs.DeleteAll();
            CreateNewSave();
            Debug.Log("[GameManager] All progress reset (DEBUG)");
        }

        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        public void DebugAddPlayTime(float hours)
        {
            TotalPlayTime += hours * 3600f;
            SaveGame();
            Debug.Log($"[GameManager] Added {hours} hours of play time (DEBUG)");
        }
        #endregion

        #region Nested Helper Classes
        [Serializable]
        private class LevelDataWrapper
        {
            public LevelData[] levels;
        }

        // Placeholder for vibration handling
        private static class Vibration
        {
            private static bool isEnabled = true;
            
            public static void SetEnabled(bool enabled)
            {
                isEnabled = enabled;
            }
            
            public static void Vibrate(long milliseconds = 50)
            {
                if (isEnabled)
                {
                    #if UNITY_ANDROID && !UNITY_EDITOR
                    AndroidVibration.Vibrate(milliseconds);
                    #elif UNITY_IOS && !UNITY_EDITOR
                    iOSHapticFeedback.Trigger();
                    #endif
                }
            }
        }

        // Placeholder for notification handling
        private static class NotificationManager
        {
            public static void RequestPermission()
            {
                #if UNITY_IOS && !UNITY_EDITOR
                // iOS notification permission request would go here
                Debug.Log("[NotificationManager] iOS notification permission requested");
                #elif UNITY_ANDROID && !UNITY_EDITOR
                // Android permissions handled at runtime
                Debug.Log("[NotificationManager] Android notification permission requested");
                #else
                Debug.Log("[NotificationManager] Notification permission requested (Editor)");
                #endif
            }
        }
        
        #if UNITY_ANDROID && !UNITY_EDITOR
        private static class AndroidVibration
        {
            private static AndroidJavaObject vibrator;
            
            static AndroidVibration()
            {
                AndroidJavaClass unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                AndroidJavaObject currentActivity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                vibrator = currentActivity.Call<AndroidJavaObject>("getSystemService", "vibrator");
            }
            
            public static void Vibrate(long milliseconds)
            {
                vibrator?.Call("vibrate", milliseconds);
            }
        }
        #endif
        
        #if UNITY_IOS && !UNITY_EDITOR
        private static class iOSHapticFeedback
        {
            [System.Runtime.InteropServices.DllImport("__Internal")]
            private static extern void _hapticFeedback();
            
            public static void Trigger()
            {
                _hapticFeedback();
            }
        }
        #endif
        #endregion
    }
}