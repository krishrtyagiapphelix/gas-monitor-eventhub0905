using System;
using System.Threading.Tasks;
using MongoDB.Driver;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

public class MongoDataService
{
    private readonly IMongoCollection<Models.TelemetryDocument> _telemetryCollection;
    private readonly IMongoCollection<Models.AlarmDocument> _alarmCollection;
    private readonly ILogger _logger;

    public MongoDataService(ILogger logger, string connectionString)
    {
        _logger = logger;

        try
        {
            // Log the connection string (mask password for security)
            string maskedConnectionString = MaskConnectionString(connectionString);
            _logger.LogInformation("Initializing MongoDB Data Service with connection: {ConnectionString}", maskedConnectionString);
            
            // Initialize MongoDB client with the connection string
            var mongoClient = new MongoClient(connectionString);
            
            // Get or create database
            var database = mongoClient.GetDatabase("oxygen_monitor");
            
            // Get or create collections
            _telemetryCollection = database.GetCollection<Models.TelemetryDocument>("telemetry");
            _alarmCollection = database.GetCollection<Models.AlarmDocument>("alarms");
            
            _logger.LogInformation("u2705 MongoDB Data Service initialized successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "u274c Error initializing MongoDB Data Service: {Message}", ex.Message);
            _logger.LogError("u274c Full exception details: {ExceptionDetails}", ex.ToString());
            throw; // Rethrow to indicate initialization failure
        }
    }

    /// <summary>
    /// Save telemetry data to MongoDB
    /// </summary>
    public async Task<string?> SaveTelemetryData(JObject data)
    {
        try
        {
            // Log the raw data for debugging
            _logger.LogInformation("Raw telemetry data received: {RawData}", data.ToString());
            
            // Try all possible device name fields in the JSON data
            string deviceName = data["device"]?.ToString() ?? 
                                data["deviceId"]?.ToString() ?? 
                                data["device_id"]?.ToString() ?? 
                                data["DeviceName"]?.ToString() ?? 
                                "unknown";
            
            _logger.LogInformation("Saving telemetry data for device {DeviceName} to MongoDB", deviceName);
            
            // Create telemetry document
            var telemetry = new Models.TelemetryDocument
            {
                DeviceName = deviceName,
                Temperature = GetDoubleValue(data, "temperature") ?? GetDoubleValue(data, "Temperature"),
                Humidity = GetDoubleValue(data, "humidity") ?? GetDoubleValue(data, "Humidity"),
                OilLevel = GetDoubleValue(data, "oilLevel") ?? GetDoubleValue(data, "oil_level") ?? GetDoubleValue(data, "OilLevel"),
                Timestamp = DateTime.UtcNow,
                RawData = data.ToString(),
                Category = DateTime.UtcNow.ToString("yyyy-MM"),
                OpenAlerts = GetIntValue(data, "open_alerts") ?? GetIntValue(data, "alerts") ?? GetIntValue(data, "OpenAlerts") ?? 0
            };
            
            // Log created document for debugging
            _logger.LogInformation("Created telemetry document: {TelemetryDoc}", 
                new { telemetry.DeviceName, telemetry.Temperature, telemetry.Humidity, telemetry.OilLevel, telemetry.Timestamp });
            
            // Insert document
            await _telemetryCollection.InsertOneAsync(telemetry);
            _logger.LogInformation("u2705 Successfully saved telemetry data with ID: {Id}", telemetry.Id);
            
            return telemetry.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "u274c Error saving telemetry data to MongoDB: {Message}", ex.Message);
            _logger.LogError("Exception details: {StackTrace}", ex.StackTrace);
            return null;
        }
    }

    /// <summary>
    /// Save alarm data to MongoDB
    /// </summary>
    public async Task<string?> SaveAlarmData(
        JObject deviceData, 
        string alarmCode, 
        string alarmDescription, 
        string? alarmValue, 
        int deviceId,
        string deviceName,
        string plantName)
    {
        try
        {
            _logger.LogInformation("Saving alarm for device {DeviceName} to MongoDB: {AlarmCode}", deviceName, alarmCode);
            
            // Create alarm document
            var alarm = new Models.AlarmDocument
            {
                AlarmId = GetAlarmIdFromCode(alarmCode),
                DeviceId = deviceId,
                AlarmValue = alarmValue ?? "0",
                CreatedBy = 1,
                IsActive = true,
                CreatedTimestamp = DateTime.UtcNow,
                UpdatedBy = 1,
                UpdatedTimestamp = DateTime.UtcNow,
                AlarmCode = alarmCode,
                AlarmDescription = alarmDescription,
                DeviceName = deviceName,
                IsRead = false,
                PlantName = plantName,
                DeviceData = deviceData.ToString()
            };
            
            // Insert document
            await _alarmCollection.InsertOneAsync(alarm);
            _logger.LogInformation("u2705 Successfully saved alarm data with ID: {Id}", alarm.Id);
            
            return alarm.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "u274c Error saving alarm data to MongoDB: {Message}", ex.Message);
            return null;
        }
    }

    public async Task SaveAlarmData(JObject alarmData)
    {
        try
        {
            // Convert JObject to AlarmDocument
            var alarmDoc = new Models.AlarmDocument
            {
                DeviceName = alarmData["deviceId"]?.ToString() ?? "unknown-device",
                CreatedTimestamp = DateTime.UtcNow,
                UpdatedTimestamp = DateTime.UtcNow,
                AlarmCode = alarmData["alert_code"]?.ToString() ?? "unknown",
                AlarmDescription = alarmData["alert_description"]?.ToString() ?? "No description",
                DeviceData = alarmData.ToString(),
                IsActive = true,
                CreatedBy = 1,
                UpdatedBy = 1,
                IsRead = false
            };

            // Insert into MongoDB
            await _alarmCollection.InsertOneAsync(alarmDoc);

            _logger.LogInformation("Alert saved to MongoDB: {AlarmCode} for {DeviceName}",
                alarmDoc.AlarmCode, alarmDoc.DeviceName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving alarm data to MongoDB: {Message}", ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Get tolerance value for a parameter from MongoDB
    /// </summary>
    public async Task<double> GetToleranceValue(string deviceId, string parameterType)
    {
        try
        {
            // Default tolerance values if nothing found in database
            var defaultTolerances = new Dictionary<string, double>
            {
                { "temperature", 0.5 },
                { "humidity", 2.0 },
                { "oilLevel", 1.0 }
            };
            
            // Connect to the same database but access the tolerances collection
            var database = _telemetryCollection.Database;
            var toleranceCollection = database.GetCollection<BsonDocument>("tolerances");
            
            // Build a filter to find tolerance setting for this device and parameter
            var filter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("deviceId", deviceId),
                Builders<BsonDocument>.Filter.Eq("type", parameterType)
            );
            
            // Try to find the tolerance value
            var result = await toleranceCollection.Find(filter).FirstOrDefaultAsync();
            
            if (result != null && result.Contains("tolerance"))
            {
                double tolerance = result["tolerance"].AsDouble;
                _logger.LogInformation("Found tolerance for {DeviceId} {ParameterType}: {Value}", 
                    deviceId, parameterType, tolerance);
                return tolerance;
            }
            
            // Return default if nothing found
            _logger.LogInformation("Using default tolerance for {DeviceId} {ParameterType}: {Value}", 
                deviceId, parameterType, defaultTolerances.GetValueOrDefault(parameterType, 1.0));
            return defaultTolerances.GetValueOrDefault(parameterType, 1.0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting tolerance value from MongoDB: {Message}", ex.Message);
            // Return default value in case of error
            return parameterType switch
            {
                "temperature" => 0.5,
                "humidity" => 2.0,
                "oilLevel" => 1.0,
                _ => 1.0
            };
        }
    }
    
    /// <summary>
    /// Get device by name
    /// </summary>
    public async Task<Models.DeviceDocument?> GetDeviceByName(string deviceName)
    {
        try
        {
            // Get MongoDB database
            var database = _telemetryCollection.Database;
            
            // Get devices collection
            var deviceCollection = database.GetCollection<Models.DeviceDocument>("devices");
            
            // Find device by name
            var device = await deviceCollection
                .Find(d => d.DeviceName == deviceName)
                .FirstOrDefaultAsync();
                
            return device;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "u274c Error getting device by name {DeviceName}: {Message}", deviceName, ex.Message);
            return null;
        }
    }

    // Helper methods
    private double? GetDoubleValue(JObject data, string propertyName)
    {
        if (data.ContainsKey(propertyName) && data[propertyName] != null)
        {
            if (double.TryParse(data[propertyName]?.ToString(), out double value))
            {
                return value;
            }
        }
        return null;
    }

    private int? GetIntValue(JObject data, string propertyName)
    {
        if (data.ContainsKey(propertyName) && data[propertyName] != null)
        {
            if (int.TryParse(data[propertyName]?.ToString(), out int value))
            {
                return value;
            }
        }
        return null;
    }

    private int GetAlarmIdFromCode(string alarmCode)
    {
        // Map alarm codes to IDs based on business logic
        switch (alarmCode)
        {
            case "IO_ALR_100": // Temperature alarm
            case "IO_ALR_101": // Temperature alarm
                return 14;
            case "IO_ALR_103": // Humidity alarm
            case "IO_ALR_104": // Humidity alarm
                return 15;
            case "IO_ALR_105": // Oil level alarm
            case "IO_ALR_106": // Oil level alarm
            case "IO_ALR_107": // Oil level alarm
            case "IO_ALR_108": // Oil empty alarm
                return 14;
            default:
                return 14; // Default ID
        }
    }

    // Helper to mask the password in connection string for logging
    private string MaskConnectionString(string connectionString)
    {
        if (string.IsNullOrEmpty(connectionString)) return "[empty]"; 
        
        try 
        {
            // Simple approach: Look for username:password@ pattern
            if (connectionString.Contains(":") && connectionString.Contains("@"))
            {
                int startIdx = connectionString.IndexOf("://") + 3;
                int endIdx = connectionString.IndexOf("@");
                
                if (startIdx > 0 && endIdx > startIdx)
                {
                    string credentials = connectionString.Substring(startIdx, endIdx - startIdx);
                    
                    // Find username and password
                    if (credentials.Contains(":"))
                    {
                        string[] parts = credentials.Split(':');
                        string maskedPassword = "******";
                        string maskedCredentials = $"{parts[0]}:{maskedPassword}";
                        
                        return connectionString.Replace(credentials, maskedCredentials);
                    }
                }
            }
        }
        catch 
        {
            // Fall back to basic masking if parsing fails
        }
        
        // Basic fallback - just indicate something is there
        return "[masked-connection-string]";
    }
}

// Document models for MongoDB - Moved to Models namespace to avoid collisions
namespace Models
{
    public class TelemetryDocument
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }
        
        public string DeviceName { get; set; } = string.Empty;
        
        public double? Temperature { get; set; }
        
        public double? Humidity { get; set; }
        
        public double? OilLevel { get; set; }
        
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        
        public string RawData { get; set; } = string.Empty;
        
        public string Category { get; set; } = string.Empty;
        
        public int? OpenAlerts { get; set; }
    }

    public class AlarmDocument
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }
        
        public int? SqlId { get; set; }
        
        public int AlarmId { get; set; }
        
        public int DeviceId { get; set; }
        
        public string AlarmValue { get; set; } = string.Empty;
        
        public int CreatedBy { get; set; }
        
        public bool IsActive { get; set; }
        
        public DateTime CreatedTimestamp { get; set; }
        
        public int? TelemetryKeyId { get; set; }
        
        public int? AlarmRootCauseId { get; set; }
        
        public int UpdatedBy { get; set; }
        
        public DateTime UpdatedTimestamp { get; set; }
        
        public string AlarmCode { get; set; } = string.Empty;
        
        public string AlarmDescription { get; set; } = string.Empty;
        
        public string DeviceName { get; set; } = string.Empty;
        
        public bool IsRead { get; set; }
        
        public string PlantName { get; set; } = string.Empty;
        
        public string DeviceData { get; set; } = string.Empty;
    }

    public class DeviceDocument
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }
        
        public string DeviceName { get; set; } = string.Empty;
        
        public string SerialNumber { get; set; } = string.Empty;
        
        public string MacId { get; set; } = string.Empty;
        
        public string? Type { get; set; }
        
        public int? PlantId { get; set; }
        
        public string? PlantName { get; set; }
        
        public bool? IsActive { get; set; }
    }
}
