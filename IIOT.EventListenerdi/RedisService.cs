using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public class RedisService
{
    private readonly ILogger _logger;
    private readonly ConnectionMultiplexer _redis;
    private readonly IDatabase _db;
    private readonly string _redisConnectionString;
    private bool _isConnected = false;

    // Track last alarm publication time per device+code to prevent batching
    private readonly Dictionary<string, DateTime> _lastAlarmPublishTime = new Dictionary<string, DateTime>();
    // Minimum time between identical alarms in milliseconds (1 second)
    private const int MIN_ALARM_INTERVAL_MS = 1000;

    /// <summary>
    /// Dictionary of device ID to plant name mappings
    /// </summary>
    private readonly Dictionary<string, string> _devicePlantMap = new Dictionary<string, string>
    {
        { "esp32_02", "Plant C" },
        { "esp32_04", "Plant D" }
    };

    public RedisService(ILogger logger)
    {
        _logger = logger;

        // Get Redis connection string from environment variable
        _redisConnectionString = Environment.GetEnvironmentVariable("RedisConnectionString") ?? "localhost:6379";
        _logger.LogInformation("Redis connection string: {ConnectionString}", _redisConnectionString);

        try
        {
            _logger.LogInformation("Connecting to Redis at {ConnectionString}", _redisConnectionString);
            _redis = ConnectionMultiplexer.Connect(_redisConnectionString);
            _db = _redis.GetDatabase();
            _isConnected = true;
            _logger.LogInformation("✅ Successfully connected to Redis");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to connect to Redis: {Message}", ex.Message);
            _isConnected = false;
        }
    }

    /// <summary>
    /// Publish telemetry data to Redis channel
    /// </summary>
    /// <param name="telemetryData">Telemetry data as JObject</param>
    /// <returns>True if successful, false otherwise</returns>
    public async Task<bool> PublishTelemetryData(JObject telemetryData)
    {
        if (!_isConnected)
        {
            _logger.LogWarning("❌ Cannot publish telemetry data: Redis not connected");
            return false;
        }

        try
        {
            // Normalize data to ensure consistent format
            string deviceId = telemetryData["deviceId"]?.ToString() ?? 
                             telemetryData["device"]?.ToString() ?? 
                             telemetryData["device_id"]?.ToString() ?? "unknown";

            string deviceName = telemetryData["deviceName"]?.ToString() ?? 
                               telemetryData["DeviceName"]?.ToString() ?? 
                               deviceId;

            // Get the plant name based on the device
            string plantName = GetPlantNameFromDevice(deviceName);

            // Add plant name to telemetry data
            telemetryData["plantName"] = plantName;

            // Convert to JSON string
            string jsonData = telemetryData.ToString(Formatting.None);

            // Publish to Redis telemetry channel
            await _db.PublishAsync("telemetry", jsonData);

            _logger.LogInformation("📡 Published telemetry data to Redis for device: {DeviceName}", deviceName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error publishing telemetry data to Redis: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Publish alarm data to Redis channel with rate limiting to prevent alarm batching
    /// </summary>
    /// <param name="alarmData">Alarm data as JObject</param>
    /// <returns>True if successful, false otherwise</returns>
    public async Task<bool> PublishAlarmData(JObject alarmData)
    {
        if (!_isConnected)
        {
            _logger.LogWarning("❌ Cannot publish alarm data: Redis not connected");
            return false;
        }

        try
        {
            // Get device information
            string deviceId = alarmData["deviceId"]?.ToString() ?? 
                             alarmData["DeviceId"]?.ToString() ?? 
                             alarmData["device"]?.ToString() ?? "unknown";

            string deviceName = alarmData["deviceName"]?.ToString() ?? 
                               alarmData["DeviceName"]?.ToString() ?? 
                               deviceId;

            string alarmCode = alarmData["alarmCode"]?.ToString() ?? 
                              alarmData["AlarmCode"]?.ToString() ?? 
                              "UNKNOWN";

            // Get the plant name based on the device
            string plantName = GetPlantNameFromDevice(deviceName);

            // Add plant name to alarm data if not already present
            if (!alarmData.ContainsKey("plantName") && !alarmData.ContainsKey("PlantName"))
            {
                alarmData["plantName"] = plantName;
            }

            // Create a unique key for this alarm type + device
            string alarmKey = $"{deviceName}:{alarmCode}";

            // Check if we've published this alarm type recently
            if (_lastAlarmPublishTime.ContainsKey(alarmKey))
            {
                // Calculate time since last publication of this alarm type
                TimeSpan timeSinceLastPublish = DateTime.UtcNow - _lastAlarmPublishTime[alarmKey];

                // If it's been less than our minimum interval, skip this publication
                if (timeSinceLastPublish.TotalMilliseconds < MIN_ALARM_INTERVAL_MS)
                {
                    _logger.LogInformation("⏱️ Rate limiting alarm {AlarmCode} for {DeviceName} - published {TimeMs}ms ago", 
                        alarmCode, deviceName, timeSinceLastPublish.TotalMilliseconds);
                    return true; // Return true since we're intentionally skipping, not failing
                }
            }

            // Update the last publish time for this alarm type
            _lastAlarmPublishTime[alarmKey] = DateTime.UtcNow;

            // Ensure the timestamp is current
            if (!alarmData.ContainsKey("createdTimestamp") && !alarmData.ContainsKey("CreatedTimestamp"))
            {
                alarmData["createdTimestamp"] = DateTime.UtcNow;
            }

            // Convert to JSON string
            string jsonData = alarmData.ToString(Formatting.None);

            // Publish to Redis alarms channel
            await _db.PublishAsync("alarms", jsonData);

            _logger.LogInformation("🚨 Published alarm {AlarmCode} to Redis for device: {DeviceName} ({PlantName})", 
                alarmCode, deviceName, plantName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error publishing alarm data to Redis: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Get plant name from device name
    /// </summary>
    /// <param name="deviceName">Device name</param>
    /// <returns>Plant name (Plant C or Plant D)</returns>
    private string GetPlantNameFromDevice(string deviceName)
    {
        if (string.IsNullOrEmpty(deviceName))
            return "Unknown Plant";
            
        if (deviceName.Contains("esp32_04"))
            return "Plant D"; // esp32_04 is associated with Plant D
        else
            return "Plant C"; // esp32_02 or any other device is associated with Plant C
    }
}
