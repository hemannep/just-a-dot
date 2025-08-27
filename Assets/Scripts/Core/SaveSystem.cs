using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.Events;

namespace JustADot.Core
{
    /// <summary>
    /// Robust save system for offline game data persistence
    /// Handles save/load, backup, encryption, and data integrity
    /// </summary>
    public class SaveSystem : MonoBehaviour
    {
        #region Constants
        // File names and paths
        private const string SAVE_FOLDER = "SaveData";
        private const string PRIMARY_SAVE_FILE = "save_main.dat";
        private const string BACKUP_SAVE_FILE = "save_backup.dat";
        private const string TEMP_SAVE_FILE = "save_temp.dat";
        private const string SETTINGS_FILE = "settings.json";
        private const string STATS_FILE = "statistics.dat";
        private const string ACHIEVEMENTS_FILE = "achievements.dat";
        
        // Encryption keys (in production, these should be more secure)
        private const string ENCRYPTION_KEY = "JustADot2024SecureKey!@#";
        private const string VALIDATION_SALT = "DotGameSalt$%^";
        
        // Save versioning
        private const int CURRENT_SAVE_VERSION = 1;
        private const int MIN_SUPPORTED_VERSION = 1;
        
        // Backup settings
        private const int MAX_BACKUP_COUNT = 3;
        private const float AUTO_BACKUP_INTERVAL = 300f; // 5 minutes
        
        // Performance settings
        private const int MAX_RETRY_ATTEMPTS = 3;
        private const float RETRY_DELAY = 0.5f;
        #endregion

        #region Events
        public static UnityEvent<bool> OnSaveComplete = new UnityEvent<bool>();
        public static UnityEvent<bool> OnLoadComplete = new UnityEvent<bool>();
        public static UnityEvent<string> OnSaveError = new UnityEvent<string>();
        public static UnityEvent<string> OnLoadError = new UnityEvent<string>();
        public static UnityEvent<float> OnSaveProgress = new UnityEvent<float>();
        public static UnityEvent OnDataCorruption = new UnityEvent();
        #endregion

        #region Private Variables
        private string savePath;
        private string backupPath;
        private bool isSaving;
        private bool isLoading;
        private Coroutine autoBackupCoroutine;
        private Queue<SaveOperation> saveQueue;
        private Dictionary<string, object> runtimeCache;
        private HashSet<string> dirtyKeys;
        private DateTime lastSaveTime;
        private DateTime lastBackupTime;
        
        // Encryption components
        private AesCryptoServiceProvider aesProvider;
        private byte[] encryptionKeyBytes;
        private byte[] encryptionIV;
        
        // Data integrity
        private Dictionary<string, string> dataChecksums;
        private bool isDataValid;
        #endregion

        #region Data Classes
        [Serializable]
        public class SaveData
        {
            public int version = CURRENT_SAVE_VERSION;
            public string timestamp;
            public string deviceId;
            public string checksum;
            
            // Game Progress
            public int currentLevel = 1;
            public int highestUnlockedLevel = 1;
            public Dictionary<int, LevelSaveData> levelProgress = new Dictionary<int, LevelSaveData>();
            
            // Player Stats
            public float totalPlayTime = 0f;
            public int totalAttempts = 0;
            public int totalCompletions = 0;
            public int totalPerfectCompletions = 0;
            public int totalHintsUsed = 0;
            public DateTime firstPlayDate;
            public DateTime lastPlayDate;
            
            // Achievements & Unlocks
            public List<string> unlockedAchievements = new List<string>();
            public List<string> unlockedThemes = new List<string>();
            public List<string> unlockedCosmetics = new List<string>();
            
            // Monetization
            public bool premiumUnlocked = false;
            public bool adsRemoved = false;
            public int totalAdsWatched = 0;
            public DateTime premiumPurchaseDate;
            
            // Statistics
            public Dictionary<string, int> statistics = new Dictionary<string, int>();
            public Dictionary<string, float> detailedStats = new Dictionary<string, float>();
        }

        [Serializable]
        public class LevelSaveData
        {
            public int levelId;
            public bool completed;
            public float bestTime;
            public int attempts;
            public int stars; // 0-3 star rating
            public bool perfectCompletion;
            public bool hintUsed;
            public DateTime firstCompletionDate;
            public DateTime lastPlayedDate;
            public List<float> allCompletionTimes = new List<float>();
        }

        [Serializable]
        public class SettingsData
        {
            public int version = CURRENT_SAVE_VERSION;
            
            // Audio
            public bool soundEnabled = true;
            public bool musicEnabled = true;
            public bool vibrationEnabled = true;
            public float masterVolume = 1f;
            public float soundVolume = 1f;
            public float musicVolume = 1f;
            
            // Display
            public string language = "en";
            public bool highContrastMode = false;
            public float dotSize = 1f;
            public int targetFrameRate = 60;
            public bool reducedMotion = false;
            
            // Notifications
            public bool notificationsEnabled = false;
            public bool dailyReminderEnabled = false;
            public string reminderTime = "19:00";
            
            // Privacy
            public bool analyticsEnabled = true;
            public bool crashReportingEnabled = true;
            
            // Accessibility
            public bool colorBlindMode = false;
            public string colorBlindType = "none"; // none, protanopia, deuteranopia, tritanopia
            public float touchSensitivity = 1f;
            public bool assistMode = false;
        }

        [Serializable]
        public class StatisticsData
        {
            public int version = CURRENT_SAVE_VERSION;
            public Dictionary<string, object> generalStats = new Dictionary<string, object>();
            public Dictionary<string, ThemeStats> themeStats = new Dictionary<string, ThemeStats>();
            public Dictionary<string, DailyStats> dailyStats = new Dictionary<string, DailyStats>();
            public SessionStats currentSession = new SessionStats();
            public List<SessionStats> recentSessions = new List<SessionStats>();
        }

        [Serializable]
        public class ThemeStats
        {
            public string themeName;
            public int levelsCompleted;
            public float totalTime;
            public int perfectCompletions;
            public float averageTime;
        }

        [Serializable]
        public class DailyStats
        {
            public string date;
            public int levelsPlayed;
            public int levelsCompleted;
            public float playTime;
            public int hintsUsed;
        }

        [Serializable]
        public class SessionStats
        {
            public DateTime startTime;
            public DateTime endTime;
            public float duration;
            public int levelsPlayed;
            public int levelsCompleted;
            public int hintsUsed;
        }

        private class SaveOperation
        {
            public string key;
            public object data;
            public Action<bool> callback;
            public bool isAsync;
            public float priority;
        }
        #endregion

        #region Unity Lifecycle
        private void Awake()
        {
            InitializeSaveSystem();
        }

        private void Start()
        {
            // Start auto-backup routine
            autoBackupCoroutine = StartCoroutine(AutoBackupRoutine());
        }

        private void OnApplicationPause(bool pauseStatus)
        {
            if (pauseStatus)
            {
                // Quick save when app is paused
                QuickSave();
            }
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus)
            {
                // Ensure data is saved when losing focus
                FlushCache();
            }
        }

        private void OnDestroy()
        {
            if (autoBackupCoroutine != null)
            {
                StopCoroutine(autoBackupCoroutine);
            }
            
            // Final save
            FlushCache();
            
            // Cleanup encryption
            aesProvider?.Dispose();
        }
        #endregion

        #region Initialization
        private void InitializeSaveSystem()
        {
            // Setup paths
            SetupSavePaths();
            
            // Initialize encryption
            InitializeEncryption();
            
            // Initialize data structures
            saveQueue = new Queue<SaveOperation>();
            runtimeCache = new Dictionary<string, object>();
            dirtyKeys = new HashSet<string>();
            dataChecksums = new Dictionary<string, string>();
            
            // Verify save directory exists
            EnsureSaveDirectoryExists();
            
            // Load existing data or create new
            LoadOrCreateSaveData();
            
            Debug.Log($"[SaveSystem] Initialized at path: {savePath}");
        }

        private void SetupSavePaths()
        {
            // Use persistent data path for mobile platforms
            #if UNITY_EDITOR
            savePath = Path.Combine(Application.dataPath, "..", "SaveData");
            #else
            savePath = Path.Combine(Application.persistentDataPath, SAVE_FOLDER);
            #endif
            
            backupPath = Path.Combine(savePath, "Backups");
        }

        private void InitializeEncryption()
        {
            try
            {
                aesProvider = new AesCryptoServiceProvider
                {
                    KeySize = 256,
                    BlockSize = 128,
                    Mode = CipherMode.CBC,
                    Padding = PaddingMode.PKCS7
                };
                
                // Generate key and IV from the base key
                using (var sha256 = SHA256.Create())
                {
                    encryptionKeyBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(ENCRYPTION_KEY));
                }
                
                using (var md5 = MD5.Create())
                {
                    encryptionIV = md5.ComputeHash(Encoding.UTF8.GetBytes(VALIDATION_SALT));
                }
                
                aesProvider.Key = encryptionKeyBytes;
                aesProvider.IV = encryptionIV;
            }
            catch (Exception e)
            {
                Debug.LogError($"[SaveSystem] Encryption initialization failed: {e.Message}");
            }
        }

        private void EnsureSaveDirectoryExists()
        {
            try
            {
                if (!Directory.Exists(savePath))
                {
                    Directory.CreateDirectory(savePath);
                }
                
                if (!Directory.Exists(backupPath))
                {
                    Directory.CreateDirectory(backupPath);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SaveSystem] Failed to create save directories: {e.Message}");
            }
        }
        #endregion

        #region Save Operations
        public void SaveGameData(SaveData data, Action<bool> callback = null)
        {
            if (isSaving)
            {
                // Queue the save operation
                saveQueue.Enqueue(new SaveOperation
                {
                    key = "GameData",
                    data = data,
                    callback = callback,
                    isAsync = true,
                    priority = 1f
                });
                return;
            }
            
            StartCoroutine(SaveGameDataAsync(data, callback));
        }

        private IEnumerator SaveGameDataAsync(SaveData data, Action<bool> callback)
        {
            isSaving = true;
            bool success = false;
            string errorMessage = null;
            
            // Update metadata
            data.timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            data.deviceId = SystemInfo.deviceUniqueIdentifier;
            
            // Calculate checksum
            data.checksum = CalculateChecksum(data);
            
            // Serialize data
            string jsonData = JsonUtility.ToJson(data, true);
            
            // Encrypt if enabled
            byte[] dataToSave = EncryptData(jsonData);
            
            // Save to temp file first (atomic operation)
            string tempPath = Path.Combine(savePath, TEMP_SAVE_FILE);
            
            // Try to write file
            bool writeComplete = false;
            bool writeSuccess = false;
            
            try
            {
                File.WriteAllBytes(tempPath, dataToSave);
                writeSuccess = true;
            }
            catch (Exception e)
            {
                errorMessage = e.Message;
                writeSuccess = false;
            }
            
            writeComplete = true;
            
            yield return new WaitUntil(() => writeComplete);
            
            if (writeSuccess)
            {
                try
                {
                    // Backup current save
                    string mainPath = Path.Combine(savePath, PRIMARY_SAVE_FILE);
                    if (File.Exists(mainPath))
                    {
                        string backupFilePath = Path.Combine(savePath, BACKUP_SAVE_FILE);
                        File.Copy(mainPath, backupFilePath, true);
                    }
                    
                    // Move temp to main
                    if (File.Exists(tempPath))
                    {
                        File.Move(tempPath, mainPath);
                        success = true;
                    }
                    
                    // Update cache
                    runtimeCache["GameData"] = data;
                    dirtyKeys.Remove("GameData");
                    
                    lastSaveTime = DateTime.Now;
                    
                    Debug.Log($"[SaveSystem] Game data saved successfully at {lastSaveTime}");
                }
                catch (Exception e)
                {
                    Debug.LogError($"[SaveSystem] Save failed: {e.Message}");
                    OnSaveError?.Invoke(e.Message);
                    success = false;
                }
            }
            else
            {
                Debug.LogError($"[SaveSystem] Write failed: {errorMessage}");
                OnSaveError?.Invoke(errorMessage);
                success = false;
            }
            
            isSaving = false;
            OnSaveComplete?.Invoke(success);
            callback?.Invoke(success);
            
            // Process queued saves
            ProcessSaveQueue();
        }

        public void SaveSettings(SettingsData settings, Action<bool> callback = null)
        {
            StartCoroutine(SaveSettingsAsync(settings, callback));
        }

        private IEnumerator SaveSettingsAsync(SettingsData settings, Action<bool> callback)
        {
            bool success = false;
            string errorMessage = null;
            
            string jsonData = JsonUtility.ToJson(settings, true);
            string filePath = Path.Combine(savePath, SETTINGS_FILE);
            
            // Settings are not encrypted for easier debugging
            byte[] dataToSave = Encoding.UTF8.GetBytes(jsonData);
            
            // Try to write file
            bool writeComplete = false;
            bool writeSuccess = false;
            
            try
            {
                File.WriteAllBytes(filePath, dataToSave);
                writeSuccess = true;
            }
            catch (Exception e)
            {
                errorMessage = e.Message;
                writeSuccess = false;
            }
            
            writeComplete = true;
            
            yield return new WaitUntil(() => writeComplete);
            
            if (writeSuccess)
            {
                runtimeCache["Settings"] = settings;
                success = true;
                Debug.Log("[SaveSystem] Settings saved successfully");
            }
            else
            {
                Debug.LogError($"[SaveSystem] Settings save failed: {errorMessage}");
                success = false;
            }
            
            callback?.Invoke(success);
        }

        public void SaveStatistics(StatisticsData stats, Action<bool> callback = null)
        {
            StartCoroutine(SaveStatisticsAsync(stats, callback));
        }

        private IEnumerator SaveStatisticsAsync(StatisticsData stats, Action<bool> callback)
        {
            bool success = false;
            string errorMessage = null;
            
            string jsonData = JsonUtility.ToJson(stats, true);
            byte[] encryptedData = EncryptData(jsonData);
            string filePath = Path.Combine(savePath, STATS_FILE);
            
            // Try to write file
            bool writeComplete = false;
            bool writeSuccess = false;
            
            try
            {
                File.WriteAllBytes(filePath, encryptedData);
                writeSuccess = true;
            }
            catch (Exception e)
            {
                errorMessage = e.Message;
                writeSuccess = false;
            }
            
            writeComplete = true;
            
            yield return new WaitUntil(() => writeComplete);
            
            if (writeSuccess)
            {
                runtimeCache["Statistics"] = stats;
                success = true;
                Debug.Log("[SaveSystem] Statistics saved successfully");
            }
            else
            {
                Debug.LogError($"[SaveSystem] Statistics save failed: {errorMessage}");
                success = false;
            }
            
            callback?.Invoke(success);
        }

        public void QuickSave()
        {
            // Synchronous save for critical moments
            try
            {
                if (runtimeCache.ContainsKey("GameData"))
                {
                    SaveData data = runtimeCache["GameData"] as SaveData;
                    if (data != null)
                    {
                        string jsonData = JsonUtility.ToJson(data, true);
                        byte[] encryptedData = EncryptData(jsonData);
                        string filePath = Path.Combine(savePath, PRIMARY_SAVE_FILE);
                        File.WriteAllBytes(filePath, encryptedData);
                        
                        Debug.Log("[SaveSystem] Quick save completed");
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SaveSystem] Quick save failed: {e.Message}");
            }
        }

        private void ProcessSaveQueue()
        {
            if (saveQueue.Count > 0 && !isSaving)
            {
                SaveOperation op = saveQueue.Dequeue();
                
                if (op.key == "GameData" && op.data is SaveData saveData)
                {
                    SaveGameData(saveData, op.callback);
                }
            }
        }

        public void FlushCache()
        {
            // Save all dirty cached data
            foreach (string key in dirtyKeys.ToList())
            {
                if (runtimeCache.ContainsKey(key))
                {
                    object data = runtimeCache[key];
                    
                    if (data is SaveData saveData)
                    {
                        SaveGameData(saveData);
                    }
                    else if (data is SettingsData settings)
                    {
                        SaveSettings(settings);
                    }
                    else if (data is StatisticsData stats)
                    {
                        SaveStatistics(stats);
                    }
                }
            }
            
            dirtyKeys.Clear();
        }
        #endregion

        #region Load Operations
        public SaveData LoadGameData()
        {
            // Check cache first
            if (runtimeCache.ContainsKey("GameData"))
            {
                return runtimeCache["GameData"] as SaveData;
            }
            
            SaveData data = LoadGameDataFromDisk();
            
            if (data != null)
            {
                runtimeCache["GameData"] = data;
            }
            
            return data;
        }

        private SaveData LoadGameDataFromDisk()
        {
            string mainPath = Path.Combine(savePath, PRIMARY_SAVE_FILE);
            string backupPath = Path.Combine(savePath, BACKUP_SAVE_FILE);
            
            // Try loading main save
            SaveData data = TryLoadSaveFile(mainPath);
            
            // If main save fails, try backup
            if (data == null && File.Exists(backupPath))
            {
                Debug.LogWarning("[SaveSystem] Main save corrupted, loading backup");
                data = TryLoadSaveFile(backupPath);
                
                if (data != null)
                {
                    // Restore backup to main
                    File.Copy(backupPath, mainPath, true);
                    OnDataCorruption?.Invoke();
                }
            }
            
            // If all fails, create new save
            if (data == null)
            {
                Debug.Log("[SaveSystem] No valid save found, creating new");
                data = CreateNewSaveData();
            }
            
            OnLoadComplete?.Invoke(data != null);
            return data;
        }

        private SaveData TryLoadSaveFile(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }
                
                byte[] encryptedData = File.ReadAllBytes(path);
                string jsonData = DecryptData(encryptedData);
                
                if (string.IsNullOrEmpty(jsonData))
                {
                    return null;
                }
                
                SaveData data = JsonUtility.FromJson<SaveData>(jsonData);
                
                // Validate data
                if (ValidateSaveData(data))
                {
                    // Check version compatibility
                    if (data.version >= MIN_SUPPORTED_VERSION)
                    {
                        // Migrate if needed
                        if (data.version < CURRENT_SAVE_VERSION)
                        {
                            data = MigrateSaveData(data);
                        }
                        
                        Debug.Log($"[SaveSystem] Loaded save from {path}");
                        return data;
                    }
                    else
                    {
                        Debug.LogError($"[SaveSystem] Save version {data.version} not supported");
                    }
                }
                else
                {
                    Debug.LogError("[SaveSystem] Save data validation failed");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SaveSystem] Failed to load save from {path}: {e.Message}");
            }
            
            return null;
        }

        public SettingsData LoadSettings()
        {
            if (runtimeCache.ContainsKey("Settings"))
            {
                return runtimeCache["Settings"] as SettingsData;
            }
            
            try
            {
                string filePath = Path.Combine(savePath, SETTINGS_FILE);
                
                if (File.Exists(filePath))
                {
                    string jsonData = File.ReadAllText(filePath);
                    SettingsData settings = JsonUtility.FromJson<SettingsData>(jsonData);
                    
                    runtimeCache["Settings"] = settings;
                    return settings;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SaveSystem] Failed to load settings: {e.Message}");
            }
            
            // Return default settings
            SettingsData defaultSettings = new SettingsData();
            runtimeCache["Settings"] = defaultSettings;
            return defaultSettings;
        }

        public StatisticsData LoadStatistics()
        {
            if (runtimeCache.ContainsKey("Statistics"))
            {
                return runtimeCache["Statistics"] as StatisticsData;
            }
            
            try
            {
                string filePath = Path.Combine(savePath, STATS_FILE);
                
                if (File.Exists(filePath))
                {
                    byte[] encryptedData = File.ReadAllBytes(filePath);
                    string jsonData = DecryptData(encryptedData);
                    StatisticsData stats = JsonUtility.FromJson<StatisticsData>(jsonData);
                    
                    runtimeCache["Statistics"] = stats;
                    return stats;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SaveSystem] Failed to load statistics: {e.Message}");
            }
            
            // Return empty statistics
            StatisticsData defaultStats = new StatisticsData();
            runtimeCache["Statistics"] = defaultStats;
            return defaultStats;
        }

        private void LoadOrCreateSaveData()
        {
            SaveData data = LoadGameData();
            
            if (data == null)
            {
                data = CreateNewSaveData();
                SaveGameData(data);
            }
        }

        private SaveData CreateNewSaveData()
        {
            return new SaveData
            {
                version = CURRENT_SAVE_VERSION,
                timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                deviceId = SystemInfo.deviceUniqueIdentifier,
                firstPlayDate = DateTime.Now,
                lastPlayDate = DateTime.Now
            };
        }
        #endregion

        #region Backup Operations
        private IEnumerator AutoBackupRoutine()
        {
            while (true)
            {
                yield return new WaitForSeconds(AUTO_BACKUP_INTERVAL);
                
                if (!isSaving)
                {
                    CreateBackup();
                }
            }
        }

        public void CreateBackup(string customName = null)
        {
            try
            {
                string mainPath = Path.Combine(savePath, PRIMARY_SAVE_FILE);
                
                if (!File.Exists(mainPath))
                {
                    Debug.LogWarning("[SaveSystem] No save file to backup");
                    return;
                }
                
                string backupFileName = string.IsNullOrEmpty(customName) 
                    ? $"backup_{DateTime.Now:yyyyMMdd_HHmmss}.dat"
                    : $"{customName}.dat";
                
                string backupFilePath = Path.Combine(backupPath, backupFileName);
                
                File.Copy(mainPath, backupFilePath, true);
                
                lastBackupTime = DateTime.Now;
                
                // Clean old backups
                CleanOldBackups();
                
                Debug.Log($"[SaveSystem] Backup created: {backupFileName}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[SaveSystem] Backup failed: {e.Message}");
            }
        }

        private void CleanOldBackups()
        {
            try
            {
                DirectoryInfo dir = new DirectoryInfo(backupPath);
                FileInfo[] files = dir.GetFiles("backup_*.dat")
                    .OrderByDescending(f => f.CreationTime)
                    .ToArray();
                
                // Keep only the most recent backups
                for (int i = MAX_BACKUP_COUNT; i < files.Length; i++)
                {
                    files[i].Delete();
                    Debug.Log($"[SaveSystem] Deleted old backup: {files[i].Name}");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SaveSystem] Failed to clean old backups: {e.Message}");
            }
        }

        public List<string> GetAvailableBackups()
        {
            List<string> backups = new List<string>();
            
            try
            {
                DirectoryInfo dir = new DirectoryInfo(backupPath);
                FileInfo[] files = dir.GetFiles("*.dat");
                
                foreach (FileInfo file in files)
                {
                    backups.Add(file.Name);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SaveSystem] Failed to get backups: {e.Message}");
            }
            
            return backups;
        }

        public bool RestoreBackup(string backupName)
        {
            try
            {
                string backupFilePath = Path.Combine(backupPath, backupName);
                string mainPath = Path.Combine(savePath, PRIMARY_SAVE_FILE);
                
                if (!File.Exists(backupFilePath))
                {
                    Debug.LogError($"[SaveSystem] Backup not found: {backupName}");
                    return false;
                }
                
                // Validate backup before restoring
                SaveData backupData = TryLoadSaveFile(backupFilePath);
                
                if (backupData != null)
                {
                    File.Copy(backupFilePath, mainPath, true);
                    
                    // Clear cache to force reload
                    runtimeCache.Clear();
                    
                    Debug.Log($"[SaveSystem] Backup restored: {backupName}");
                    return true;
                }
                else
                {
                    Debug.LogError($"[SaveSystem] Backup validation failed: {backupName}");
                    return false;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SaveSystem] Restore failed: {e.Message}");
                return false;
            }
        }
        #endregion

        #region Encryption & Security
        private byte[] EncryptData(string plainText)
        {
            try
            {
                if (aesProvider == null)
                {
                    // Fallback to unencrypted
                    return Encoding.UTF8.GetBytes(plainText);
                }
                
                byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
                
                using (var encryptor = aesProvider.CreateEncryptor())
                {
                    return encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SaveSystem] Encryption failed: {e.Message}");
                // Fallback to unencrypted
                return Encoding.UTF8.GetBytes(plainText);
            }
        }

        private string DecryptData(byte[] cipherBytes)
        {
            try
            {
                if (aesProvider == null)
                {
                    // Try as unencrypted
                    return Encoding.UTF8.GetString(cipherBytes);
                }
                
                using (var decryptor = aesProvider.CreateDecryptor())
                {
                    byte[] plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
                    return Encoding.UTF8.GetString(plainBytes);
                }
            }
            catch (CryptographicException)
            {
                // Try as unencrypted (for backward compatibility)
                try
                {
                    return Encoding.UTF8.GetString(cipherBytes);
                }
                catch
                {
                    Debug.LogError("[SaveSystem] Decryption failed - data may be corrupted");
                    return null;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SaveSystem] Decryption error: {e.Message}");
                return null;
            }
        }

        private string CalculateChecksum(SaveData data)
        {
            try
            {
                // Create a string representation of important data
                string dataString = $"{data.currentLevel}{data.highestUnlockedLevel}" +
                                  $"{data.totalPlayTime}{data.totalCompletions}{data.deviceId}";
                
                using (var sha256 = SHA256.Create())
                {
                    byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(dataString + VALIDATION_SALT));
                    return Convert.ToBase64String(hashBytes);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SaveSystem] Checksum calculation failed: {e.Message}");
                return "";
            }
        }

        private bool ValidateSaveData(SaveData data)
        {
            if (data == null) return false;
            
            // Basic validation
            if (data.currentLevel < 1 || data.currentLevel > 100) return false;
            if (data.highestUnlockedLevel < 1 || data.highestUnlockedLevel > 100) return false;
            if (data.totalPlayTime < 0) return false;
            if (data.highestUnlockedLevel < data.currentLevel) return false;
            
            // Checksum validation (optional, can be disabled for debugging)
            #if !UNITY_EDITOR
            string expectedChecksum = CalculateChecksum(data);
            if (!string.IsNullOrEmpty(data.checksum) && data.checksum != expectedChecksum)
            {
                Debug.LogWarning("[SaveSystem] Checksum mismatch - possible data tampering");
                // You can choose to reject the save here or just log the warning
                // return false;
            }
            #endif
            
            return true;
        }
        #endregion

        #region Data Migration
        private SaveData MigrateSaveData(SaveData oldData)
        {
            Debug.Log($"[SaveSystem] Migrating save data from v{oldData.version} to v{CURRENT_SAVE_VERSION}");
            
            // Handle migration based on version differences
            // This is where you would add migration logic when updating save format
            
            switch (oldData.version)
            {
                case 1:
                    // Current version, no migration needed
                    break;
                    
                // Add future migration cases here
                // case 2:
                //     MigrateFromV1ToV2(oldData);
                //     break;
            }
            
            oldData.version = CURRENT_SAVE_VERSION;
            return oldData;
        }
        #endregion

        #region Cache Management
        public T GetCachedData<T>(string key) where T : class
        {
            if (runtimeCache.ContainsKey(key))
            {
                return runtimeCache[key] as T;
            }
            return null;
        }

        public void SetCachedData(string key, object data, bool markDirty = true)
        {
            runtimeCache[key] = data;
            
            if (markDirty)
            {
                dirtyKeys.Add(key);
            }
        }

        public void ClearCache()
        {
            runtimeCache.Clear();
            dirtyKeys.Clear();
            Debug.Log("[SaveSystem] Cache cleared");
        }

        public bool IsCached(string key)
        {
            return runtimeCache.ContainsKey(key);
        }
        #endregion

        #region Export/Import
        public string ExportSaveData()
        {
            try
            {
                SaveData data = LoadGameData();
                if (data != null)
                {
                    // Create export package
                    ExportPackage package = new ExportPackage
                    {
                        saveData = data,
                        settings = LoadSettings(),
                        statistics = LoadStatistics(),
                        exportDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        gameVersion = Application.version
                    };
                    
                    string jsonData = JsonUtility.ToJson(package, true);
                    
                    // Encode to base64 for easy sharing
                    byte[] bytes = Encoding.UTF8.GetBytes(jsonData);
                    return Convert.ToBase64String(bytes);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SaveSystem] Export failed: {e.Message}");
            }
            
            return null;
        }

        public bool ImportSaveData(string base64Data)
        {
            try
            {
                // Decode from base64
                byte[] bytes = Convert.FromBase64String(base64Data);
                string jsonData = Encoding.UTF8.GetString(bytes);
                
                // Parse export package
                ExportPackage package = JsonUtility.FromJson<ExportPackage>(jsonData);
                
                if (package != null && package.saveData != null)
                {
                    // Validate before importing
                    if (ValidateSaveData(package.saveData))
                    {
                        // Create backup before import
                        CreateBackup("pre_import_backup");
                        
                        // Import data
                        SaveGameData(package.saveData);
                        
                        if (package.settings != null)
                        {
                            SaveSettings(package.settings);
                        }
                        
                        if (package.statistics != null)
                        {
                            SaveStatistics(package.statistics);
                        }
                        
                        Debug.Log("[SaveSystem] Save data imported successfully");
                        return true;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SaveSystem] Import failed: {e.Message}");
            }
            
            return false;
        }

        [Serializable]
        private class ExportPackage
        {
            public SaveData saveData;
            public SettingsData settings;
            public StatisticsData statistics;
            public string exportDate;
            public string gameVersion;
        }
        #endregion

        #region Cloud Save Support (Preparation)
        public CloudSaveData PrepareCloudSave()
        {
            return new CloudSaveData
            {
                saveData = LoadGameData(),
                settings = LoadSettings(),
                statistics = LoadStatistics(),
                deviceId = SystemInfo.deviceUniqueIdentifier,
                platform = Application.platform.ToString(),
                timestamp = DateTime.Now
            };
        }

        public void ApplyCloudSave(CloudSaveData cloudData)
        {
            if (cloudData == null) return;
            
            // Check if cloud save is newer
            SaveData localData = LoadGameData();
            if (localData != null && !string.IsNullOrEmpty(localData.timestamp))
            {
                DateTime localTime = DateTime.Parse(localData.timestamp);
                if (cloudData.timestamp > localTime)
                {
                    // Cloud save is newer, apply it
                    SaveGameData(cloudData.saveData);
                    SaveSettings(cloudData.settings);
                    SaveStatistics(cloudData.statistics);
                    
                    Debug.Log("[SaveSystem] Cloud save applied");
                }
                else
                {
                    Debug.Log("[SaveSystem] Local save is newer, keeping local");
                }
            }
        }

        [Serializable]
        public class CloudSaveData
        {
            public SaveData saveData;
            public SettingsData settings;
            public StatisticsData statistics;
            public string deviceId;
            public string platform;
            public DateTime timestamp;
        }
        #endregion

        #region Utility Methods
        public void DeleteAllSaveData()
        {
            try
            {
                // Create backup before deletion
                CreateBackup("before_delete_all");
                
                // Delete all save files
                string[] filesToDelete = {
                    Path.Combine(savePath, PRIMARY_SAVE_FILE),
                    Path.Combine(savePath, BACKUP_SAVE_FILE),
                    Path.Combine(savePath, SETTINGS_FILE),
                    Path.Combine(savePath, STATS_FILE),
                    Path.Combine(savePath, ACHIEVEMENTS_FILE)
                };
                
                foreach (string file in filesToDelete)
                {
                    if (File.Exists(file))
                    {
                        File.Delete(file);
                    }
                }
                
                // Clear cache
                ClearCache();
                
                Debug.Log("[SaveSystem] All save data deleted");
            }
            catch (Exception e)
            {
                Debug.LogError($"[SaveSystem] Failed to delete save data: {e.Message}");
            }
        }

        public long GetSaveFileSize()
        {
            long totalSize = 0;
            
            try
            {
                DirectoryInfo dirInfo = new DirectoryInfo(savePath);
                FileInfo[] files = dirInfo.GetFiles("*.dat", SearchOption.AllDirectories);
                
                foreach (FileInfo file in files)
                {
                    totalSize += file.Length;
                }
                
                FileInfo settingsFile = new FileInfo(Path.Combine(savePath, SETTINGS_FILE));
                if (settingsFile.Exists)
                {
                    totalSize += settingsFile.Length;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SaveSystem] Failed to calculate save size: {e.Message}");
            }
            
            return totalSize;
        }

        public string GetSaveFileSizeFormatted()
        {
            long bytes = GetSaveFileSize();
            string[] sizes = { "B", "KB", "MB", "GB" };
            int order = 0;
            double size = bytes;
            
            while (size >= 1024 && order < sizes.Length - 1)
            {
                order++;
                size = size / 1024;
            }
            
            return $"{size:0.##} {sizes[order]}";
        }

        public DateTime GetLastSaveTime()
        {
            return lastSaveTime;
        }

        public bool HasSaveData()
        {
            string mainPath = Path.Combine(savePath, PRIMARY_SAVE_FILE);
            return File.Exists(mainPath);
        }

        public SaveSystemInfo GetSystemInfo()
        {
            return new SaveSystemInfo
            {
                saveLocation = savePath,
                totalSaveSize = GetSaveFileSize(),
                lastSaveTime = lastSaveTime,
                lastBackupTime = lastBackupTime,
                backupCount = GetAvailableBackups().Count,
                isEncryptionEnabled = aesProvider != null,
                saveVersion = CURRENT_SAVE_VERSION
            };
        }

        [Serializable]
        public class SaveSystemInfo
        {
            public string saveLocation;
            public long totalSaveSize;
            public DateTime lastSaveTime;
            public DateTime lastBackupTime;
            public int backupCount;
            public bool isEncryptionEnabled;
            public int saveVersion;
        }
        #endregion

        #region Debug Methods
        #if UNITY_EDITOR
        [UnityEditor.MenuItem("Just a Dot/Save System/Open Save Folder")]
        private static void OpenSaveFolder()
        {
            string path = Path.Combine(Application.persistentDataPath, SAVE_FOLDER);
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
            UnityEditor.EditorUtility.RevealInFinder(path);
        }

        [UnityEditor.MenuItem("Just a Dot/Save System/Create Debug Save")]
        private static void CreateDebugSave()
        {
            SaveSystem saveSystem = FindFirstObjectByType<SaveSystem>();
            if (saveSystem == null)
            {
                GameObject go = new GameObject("SaveSystem");
                saveSystem = go.AddComponent<SaveSystem>();
            }
            
            SaveData debugData = new SaveData
            {
                currentLevel = 50,
                highestUnlockedLevel = 75,
                totalPlayTime = 3600f,
                totalCompletions = 70,
                totalAttempts = 150
            };
            
            // Add some level progress
            for (int i = 1; i <= 70; i++)
            {
                debugData.levelProgress[i] = new LevelSaveData
                {
                    levelId = i,
                    completed = true,
                    bestTime = UnityEngine.Random.Range(2f, 30f),
                    attempts = UnityEngine.Random.Range(1, 5),
                    stars = UnityEngine.Random.Range(1, 4),
                    perfectCompletion = UnityEngine.Random.Range(0, 2) == 1,
                    firstCompletionDate = DateTime.Now.AddDays(-UnityEngine.Random.Range(1, 30))
                };
            }
            
            saveSystem.SaveGameData(debugData, success =>
            {
                Debug.Log($"Debug save created: {success}");
            });
        }

        [UnityEditor.MenuItem("Just a Dot/Save System/Corrupt Save File")]
        private static void CorruptSaveFile()
        {
            string path = Path.Combine(Application.persistentDataPath, SAVE_FOLDER, PRIMARY_SAVE_FILE);
            if (File.Exists(path))
            {
                byte[] data = File.ReadAllBytes(path);
                // Corrupt some bytes
                for (int i = 0; i < Mathf.Min(10, data.Length); i++)
                {
                    data[UnityEngine.Random.Range(0, data.Length)] = (byte)UnityEngine.Random.Range(0, 256);
                }
                File.WriteAllBytes(path, data);
                Debug.Log("Save file corrupted for testing");
            }
        }
        #endif
        
        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        public void LogSaveData()
        {
            SaveData data = LoadGameData();
            if (data != null)
            {
                Debug.Log($"[SaveSystem] Current Save Data:");
                Debug.Log($"  - Current Level: {data.currentLevel}");
                Debug.Log($"  - Highest Unlocked: {data.highestUnlockedLevel}");
                Debug.Log($"  - Total Play Time: {data.totalPlayTime:F2} seconds");
                Debug.Log($"  - Total Completions: {data.totalCompletions}");
                Debug.Log($"  - Achievements: {data.unlockedAchievements.Count}");
                Debug.Log($"  - Device ID: {data.deviceId}");
                Debug.Log($"  - Last Save: {data.timestamp}");
            }
        }
        
        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        public void TestEncryption()
        {
            string testData = "This is a test string for encryption";
            byte[] encrypted = EncryptData(testData);
            string decrypted = DecryptData(encrypted);
            
            bool success = testData == decrypted;
            Debug.Log($"[SaveSystem] Encryption test: {(success ? "PASSED" : "FAILED")}");
            
            if (!success)
            {
                Debug.Log($"  Original: {testData}");
                Debug.Log($"  Decrypted: {decrypted}");
            }
        }
        #endregion
    }
}