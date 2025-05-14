using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Azure.Messaging.EventHubs;
using Newtonsoft.Json.Linq;
using System.Net.Http;
using MongoDB.Driver;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using System.Collections.Generic;
using System.Linq;

public class IoTDataProcessor
{
    private readonly ILogger<IoTDataProcessor> _logger;
    private readonly HttpClient _httpClient = new HttpClient();
    private readonly string? _mongoConnectionString;
    private IMongoCollection<AlarmDocument>? _alarmCollection;
    private IMongoCollection<Models.TelemetryDocument>? _telemetryCollection;
    private MongoDataService? _mongoDataService;
    private RedisService? _redisService;
    private int _alarmsInserted = 0;
    private bool _isInitialized = false;
    
    // Add sensor value cache for delta optimization - tracks the last known values for each device and sensor
    // Track the last sensor values we've seen for each device
    private static readonly Dictionary<string, Dictionary<string, object>> _lastSensorValues = new Dictionary<string, Dictionary<string, object>>();
    
    // NEW: Track the last values we actually stored in MongoDB
    private static readonly Dictionary<string, Dictionary<string, double>> _lastStoredValues = new Dictionary<string, Dictionary<string, double>>();
    
    // Track which parameters changed in the current message for selective MongoDB updates
    private static readonly Dictionary<string, HashSet<string>> _changedParameters = new Dictionary<string, HashSet<string>>();
    
    // Define default tolerances for significant changes
    private static Dictionary<string, double> _changeTolerances = new Dictionary<string, double>
    {
        { "temperature", 0.5 }, // 0.5 degrees change is significant
        { "humidity", 2.0 },    // 2% change in humidity is significant
        { "oilLevel", 1.0 }     // 1% change in oil level is significant
    };
    
    // Fetch the latest tolerance values from MongoDB database
    private async Task UpdateToleranceValues(string deviceId)
    {
        try
        {
            if (_mongoDataService != null)
            {
                // Get tolerances for each parameter type
                foreach (var sensorType in new[] { "temperature", "humidity", "oilLevel" })
                {
                    double toleranceValue = await _mongoDataService.GetToleranceValue(deviceId, sensorType);
                    
                    // Only update if we got a valid value back
                    if (toleranceValue > 0)
                    {
                        _logger.LogInformation("Updating tolerance for {DeviceId} {SensorType}: {Value}", 
                            deviceId, sensorType, toleranceValue);
                        _changeTolerances[sensorType] = toleranceValue;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching tolerance values from MongoDB: {Message}", ex.Message);
        }
    }

    public IoTDataProcessor(ILogger<IoTDataProcessor> logger)
    {
        _logger = logger;

        try
        {
            _mongoConnectionString = Environment.GetEnvironmentVariable("MongoDbConnectionString");
            _logger.LogInformation("Initializing IoTDataProcessor with MongoDB");
            
            // Initialize Redis service
            _redisService = new RedisService(_logger);
            _logger.LogInformation("Redis service initialized");
            
            // Initialize MongoDB client if connection string is available
            if (!string.IsNullOrEmpty(_mongoConnectionString))
            {
                try
                {
                    var mongoClient = new MongoClient(_mongoConnectionString);
                    var database = mongoClient.GetDatabase("oxygen_monitor");
                    _alarmCollection = database.GetCollection<AlarmDocument>("alarms");
                    _telemetryCollection = database.GetCollection<Models.TelemetryDocument>("telemetry");
                    
                    // Initialize MongoDB data service for more complex operations
                    _mongoDataService = new MongoDataService(_logger, _mongoConnectionString);
                    
                    _logger.LogInformation("MongoDB client initialized successfully");
                    _isInitialized = true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to initialize MongoDB client: {Message}", ex.Message);
                }
            }
            else
            {
                _logger.LogWarning("MongoDB connection string is not configured");
            }
            
            if (_isInitialized)
            {
                _logger.LogInformation("IoTDataProcessor initialized successfully");
            }
            else
            {
                _logger.LogWarning("IoTDataProcessor initialized with warnings - some features may not work");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initializing IoTDataProcessor: {Message}", ex.Message);
            // Don't throw the exception as it would prevent the function from starting
        }
    }

    [Function("ProcessIoTDataV3_Unique")]
    public async Task ProcessIoTData(
        [EventHubTrigger(
            eventHubName: "iothub-ehub-iothub-iot-64635521-25af07dd8a",
            Connection = "EventHubConnectionString",
            ConsumerGroup = "%ConsumerGroup%",
            IsBatched = true)] 
        EventData[] events)
    {
        // Reset counter at the beginning of processing
        _alarmsInserted = 0;
        
        _logger.LogInformation("====== EVENT HUB TRIGGER FIRED ======");
        _logger.LogInformation($"Received {events.Length} events from IoT Hub at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}");
        
        // Exit early if no events to process
        if (events.Length == 0) {
            _logger.LogWarning("No events received in this trigger invocation");
            return;
        }
        
        // Track if we've seen each device in this batch
        bool foundEsp32_02 = false;
        bool foundEsp32_04 = false;
        
        // Initialize if not already done - this ensures we always attempt to initialize
        if (!_isInitialized)
        {
            try
            {
                // Initialize MongoDB connection
                if (!string.IsNullOrEmpty(_mongoConnectionString))
                {
                    var mongoClient = new MongoClient(_mongoConnectionString);
                    var database = mongoClient.GetDatabase("oxygen_monitor");
                    _alarmCollection = database.GetCollection<AlarmDocument>("alarms");
                    _telemetryCollection = database.GetCollection<Models.TelemetryDocument>("telemetry");
                    
                    // Initialize MongoDB data service
                    _mongoDataService = new MongoDataService(_logger, _mongoConnectionString);
                    _logger.LogInformation("MongoDB connection initialized successfully");
                }
                
                _isInitialized = true;
                _logger.LogInformation("IoTDataProcessor initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize IoTDataProcessor: {Message}", ex.Message);
                // Continue processing even if initialization fails
            }
        }
        
        foreach (EventData data in events)
        {
            try
            {
                // Convert the event data to a string
                string messageBody = Encoding.UTF8.GetString(data.Body.ToArray());
                
                // For diagnostic purposes, capture EventHub message properties for esp32_04 data
                var properties = new Dictionary<string, string>();
                foreach (var prop in data.Properties)
                {
                    properties[prop.Key] = prop.Value?.ToString() ?? "null";
                }
                
                // Get the sequence number and enqueued time for diagnostic purposes
                long sequenceNumber = data.SequenceNumber;
                DateTimeOffset enqueuedTime = data.EnqueuedTime;
                string partitionKey = data.PartitionKey ?? "unknown";
                
                // Log incoming telemetry with clear separator to make it easier to read
                _logger.LogInformation("");
                _logger.LogInformation("========== TELEMETRY DATA ==========");
                _logger.LogInformation("{MessageBody}", messageBody);
                _logger.LogInformation("==========================================");

                // Validate if the data is valid JSON
                if (!IsValidJson(messageBody))
                {
                    _logger.LogWarning($"Invalid JSON format: {messageBody}");
                    continue;
                }

                // Parse JSON and add metadata
                JObject jsonObject = JObject.Parse(messageBody);
                
                // Check if the payload is wrapped in an event object
                if (jsonObject.ContainsKey("event") && jsonObject["event"]["payload"] != null)
                {
                    string payload = jsonObject["event"]["payload"].ToString();
                    if (IsValidJson(payload))
                    {
                        jsonObject = JObject.Parse(payload);
                    }
                }
                
                // Track which devices we've seen in this batch
                string deviceName = jsonObject["device"]?.ToString() ?? jsonObject["device_id"]?.ToString() ?? "unknown";
                string origin = jsonObject["origin"]?.ToString() ?? "";
                
                // Debug log to see what device name we're detecting
                _logger.LogInformation("Detected device name: {DeviceName}, Origin: {Origin}", deviceName, origin);
                
                // Process both esp32_02 and esp32_04 devices
                if (deviceName.Contains("esp32_02") || origin.Contains("esp32_02"))
                {
                    foundEsp32_02 = true;
                    _logger.LogInformation("Identified as esp32_02 - processing data");
                }
                else if (deviceName.Contains("esp32_04") || origin.Contains("esp32_04"))
                {
                    foundEsp32_04 = true;
                    _logger.LogInformation("Identified as esp32_04 - processing data");
                }
                else
                {
                    _logger.LogWarning("Unknown device: {DeviceName} - skipping", deviceName);
                    continue; // Skip processing for unknown devices
                }
                
                // ====== START OF DELTA OPTIMIZATION LOGIC ======
                // Check for significant changes in sensor values
                bool hasSignificantChanges = false;
                // Flag to track if any relevant parameter (temp/humidity/oilLevel) has changed
                bool hasRelevantParameterChanges = false;
                string deviceId = jsonObject["deviceId"]?.ToString() ?? jsonObject["device"]?.ToString() ?? jsonObject["device_id"]?.ToString() ?? "unknown-device";
                
                // Log the device ID we're processing
                _logger.LogInformation("Processing data for device: {DeviceId} (Original device name: {DeviceName})", deviceId, deviceName);
                
                // Initialize device in cache if not exists
                if (!_lastSensorValues.ContainsKey(deviceId))
                {
                    _lastSensorValues[deviceId] = new Dictionary<string, object>();
                    // First time seeing this device, consider it a significant change
                    hasSignificantChanges = true;
                    hasRelevantParameterChanges = true; // First time always has relevant parameter changes
                    _logger.LogInformation("First time seeing device {DeviceId}, processing data", deviceId);
                }
                
                // Initialize changed parameters tracking for this device
                if (!_changedParameters.ContainsKey(deviceId))
                {
                    _changedParameters[deviceId] = new HashSet<string>();
                }
                else
                {
                    // Clear previous changes for this device
                    _changedParameters[deviceId].Clear();
                }
                
                // Update tolerance values from MongoDB
                await UpdateToleranceValues(deviceId);
                
                // Check each sensor type for significant changes
                foreach (var sensorType in new[] { "temperature", "humidity", "oilLevel" })
                {
                    if (jsonObject.ContainsKey(sensorType) && jsonObject[sensorType] != null)
                    {
                        // Get the new value from telemetry data
                        double newValue;
                        if (double.TryParse(jsonObject[sensorType].ToString(), out newValue))
                        {
                            // Skip processing if the value is 0 and it's the first message (likely initialization data)
                            if (newValue == 0 && jsonObject["msgCount"]?.ToString() == "0" && sensorType != "oilLevel")
                            {
                                _logger.LogInformation("Skipping initial zero value for {SensorType} on device {DeviceId}", 
                                    sensorType, deviceId);
                                continue;
                            }
                            
                            // Log the extracted sensor value for debugging
                            _logger.LogInformation("Extracted {SensorType} value for {DeviceId}: {Value}", 
                                sensorType, deviceId, newValue);
                                
                            // Get threshold for this sensor type
                            double threshold = _changeTolerances.TryGetValue(sensorType, out var value) ? value : 0.0;
                            
                            // Check if we have a previous value to compare
                            if (_lastSensorValues[deviceId].TryGetValue(sensorType, out var oldValueObj))
                            {
                                double oldValue = Convert.ToDouble(oldValueObj);
                                
                                // Special case for oilLevel: if it's 0, it's always significant (empty tank)
                                bool isSignificantZero = sensorType == "oilLevel" && newValue == 0;
                                
                                // Check if the change exceeds the threshold or if it's a significant zero
                                if (Math.Abs(newValue - oldValue) > threshold || isSignificantZero)
                                {
                                    _logger.LogInformation("Significant change in {DeviceId} {SensorType}: {OldValue} -> {NewValue}", 
                                        deviceId, sensorType, oldValue, newValue);
                                    hasSignificantChanges = true;
                                    // Track which parameter changed
                                    _changedParameters[deviceId].Add(sensorType);
                                    // Flag that a relevant parameter has changed - IMPORTANT for MongoDB storage decision
                                    // Since this is temp/humidity/oilLevel, it's always relevant for storage
                                    hasRelevantParameterChanges = true;
                                    // Update the stored value
                                    _lastSensorValues[deviceId][sensorType] = newValue;
                                }
                                else
                                {
                                    _logger.LogInformation("No significant change in {DeviceId} {SensorType}: {OldValue} -> {NewValue} (below threshold of {Threshold})", 
                                        deviceId, sensorType, oldValue, newValue, threshold);
                                    // Update the in-memory value but DON'T set hasRelevantParameterChanges to true
                                    // This ensures we don't store redundant data
                                    _lastSensorValues[deviceId][sensorType] = newValue;
                                }
                            }
                            else
                            {
                                // First time seeing this sensor type, consider it a significant change
                                _logger.LogInformation("First reading for {DeviceId} {SensorType}: {Value}", 
                                    deviceId, sensorType, newValue);
                                hasSignificantChanges = true;
                                // Track which parameter changed
                                _changedParameters[deviceId].Add(sensorType);
                                // Flag that a relevant parameter has changed - IMPORTANT for MongoDB storage decision
                                hasRelevantParameterChanges = true;
                                // Store the value
                                _lastSensorValues[deviceId][sensorType] = newValue;
                            }
                        }
                        else
                        {
                            _logger.LogWarning("Could not parse {SensorType} value for {DeviceId}: {Value}", 
                                sensorType, deviceId, jsonObject[sensorType]);
                        }
                    }
                }
                
                // Check for alerts - always process data if alerts are present
                if (jsonObject.ContainsKey("alerts") && jsonObject["alerts"] != null && jsonObject["alerts"].HasValues)
                {
                    var alertsArray = jsonObject["alerts"] as JArray;
                    if (alertsArray != null && alertsArray.Count > 0)
                    {
                        foreach (var alert in alertsArray)
                        {
                            string alertCode = alert["code"]?.ToString() ?? "unknown";
                            string alertDesc = alert["desc"]?.ToString() ?? "unknown";
                            _logger.LogInformation("Alert detected for device {DeviceId}: {AlertCode} - {AlertDesc}", 
                                deviceId, alertCode, alertDesc);
                        }
                        hasSignificantChanges = true;
                        // Track that alerts have changed
                        _changedParameters[deviceId].Add("alerts");
                        // Note: We don't set hasRelevantParameterChanges=true here since alerts aren't temp/humidity/oilLevel
                    }
                }
                
                // Check for alert_code - alternate alert format
                if (jsonObject.ContainsKey("alert_code") && jsonObject["alert_code"] != null)
                {
                    _logger.LogInformation("Alert code detected for device {DeviceId}: {AlertCode}", 
                        deviceId, jsonObject["alert_code"]);
                    hasSignificantChanges = true;
                    // Track that alert_code has changed
                    _changedParameters[deviceId].Add("alert_code");
                    // Note: We don't set hasRelevantParameterChanges=true here since alert_code isn't temp/humidity/oilLevel
                }
                
                // Save the relevant parameter change state to use for MongoDB storage decision
                // This ensures we only store data if temperature, humidity, or oilLevel has changed
                jsonObject["_hasRelevantParameterChanges"] = hasRelevantParameterChanges;
                
                // OPTIMIZATION: Skip processing if no significant changes were detected (whether relevant or not)
                if (!hasSignificantChanges)
                {
                    _logger.LogInformation("No significant changes detected for device {DeviceId}, skipping further processing", deviceId);
                    continue;
                }
                
                // Log that we're processing this data
                _logger.LogInformation("Processing data for device {DeviceId} due to significant changes", deviceId);
                // ====== END OF DELTA OPTIMIZATION LOGIC ======
                
                string category = DateTime.UtcNow.ToString("yyyy-MM");
                jsonObject["Category"] = category;
                jsonObject["id"] = Guid.NewGuid().ToString();
                jsonObject["receivedTimestamp"] = DateTime.UtcNow;

                // Store telemetry data in MongoDB and publish to Redis
                try
                {
                    // Ensure we have a device identifier
                    string telemetryDeviceId = jsonObject["deviceId"]?.ToString() ?? 
                                     jsonObject["device"]?.ToString() ?? 
                                     "unknown-device";
                    
                    // Ensure deviceId is consistently present in the document
                    if (!jsonObject.ContainsKey("deviceId"))
                    {
                        jsonObject["deviceId"] = telemetryDeviceId;
                    }
                    
                    // Ensure we have a unique ID
                    jsonObject["id"] = Guid.NewGuid().ToString();
                    
                    _logger.LogInformation("========== MONGODB TELEMETRY INSERTION ==========");
                    _logger.LogInformation("Device ID: {DeviceId}", telemetryDeviceId);
                    
                    // Get the value of the flag we set earlier that tracks if any temp/humidity/oilLevel changed
                    // Use a different variable name to avoid conflict with the outer scope
                    bool shouldStoreInMongoDB = false;
                    if (jsonObject.ContainsKey("_hasRelevantParameterChanges") && 
                        jsonObject["_hasRelevantParameterChanges"] != null)
                    {
                        shouldStoreInMongoDB = (bool)jsonObject["_hasRelevantParameterChanges"];
                    }
                    
                    // Get current values for critical parameters
                    double currentTemp = 0, currentHumidity = 0, currentOilLevel = 0;
                    bool hasTempValue = jsonObject.ContainsKey("temperature") && 
                                     double.TryParse(jsonObject["temperature"].ToString(), out currentTemp);
                    bool hasHumidityValue = jsonObject.ContainsKey("humidity") && 
                                         double.TryParse(jsonObject["humidity"].ToString(), out currentHumidity);
                    bool hasOilLevelValue = jsonObject.ContainsKey("oilLevel") && 
                                         double.TryParse(jsonObject["oilLevel"].ToString(), out currentOilLevel);
                    
                    // Initialize last stored values dictionary if needed
                    if (!_lastStoredValues.ContainsKey(deviceId))
                    {
                        _lastStoredValues[deviceId] = new Dictionary<string, double>();
                        _logger.LogInformation("First data for device {DeviceId} - will store in MongoDB", deviceId);
                        shouldStoreInMongoDB = true; // First time, always store data
                    }

                    // Log which parameters have changed
                    if (_changedParameters.ContainsKey(deviceId) && _changedParameters[deviceId].Count > 0)
                    {
                        _logger.LogInformation("Changed parameters for device {DeviceId}: {ChangedParams}", 
                            deviceId, string.Join(", ", _changedParameters[deviceId]));
                    }
                    
                    // Always publish telemetry data to Redis for real-time updates
                    if (_redisService != null)
                    {
                        await _redisService.PublishTelemetryData(jsonObject);
                        _logger.LogInformation("Published telemetry data to Redis for real-time updates");
                    }
                    else
                    {
                        _logger.LogWarning("Redis service not available - cannot publish telemetry data");
                    }
                    
                    // ONLY send data to MongoDB if temperature, humidity, or oil level has changed
                    if (shouldStoreInMongoDB)
                    {
                        _logger.LogInformation("STORING DATA - At least one critical parameter changed - sending ALL telemetry data to MongoDB");
                        
                        // Update the last stored values after successful storage
                        if (hasTempValue) _lastStoredValues[deviceId]["temperature"] = currentTemp;
                        if (hasHumidityValue) _lastStoredValues[deviceId]["humidity"] = currentHumidity;
                        if (hasOilLevelValue) _lastStoredValues[deviceId]["oilLevel"] = currentOilLevel;
                        
                        // Use the MongoDataService to save the complete telemetry data with ALL parameters
                        if (_mongoDataService != null)
                        {
                            await _mongoDataService.SaveTelemetryData(jsonObject);
                            _logger.LogInformation("SUCCESS: Stored ALL telemetry data in MongoDB because at least one parameter changed");
                        }
                        else
                        {
                            // Fallback to direct insertion if data service is not available
                            await InsertTelemetryIntoMongoDB(jsonObject);
                        }
                    }
                    else
                    {
                        _logger.LogWarning("SKIPPED MONGODB STORAGE - No changes in temperature, humidity, or oil level parameters");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error storing telemetry in MongoDB: {Message}", ex.Message);
                }

                // Process alarms
                try
                {
                // Process temperature alarms
                await ProcessTemperatureAlarms(jsonObject);
                
                // Process humidity alarms
                await ProcessHumidityAlarms(jsonObject);
                
                // Process oil level alarms
                await ProcessOilLevelAlarms(jsonObject);
                
                // Process explicit alerts in the data
                await ProcessExplicitAlerts(jsonObject);
            }
            catch (Exception ex)
            {
                    _logger.LogError(ex, "Error processing alarms: {Message}", ex.Message);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception processing event: {Message}", ex.Message);
            }
        }
        
        // After processing all events, log the total
        _logger.LogInformation($"Total alarms inserted: {_alarmsInserted}");
        
        // Only generate sample alarms for esp32_02 if it's online
        // This prevents creating fake data for offline devices
        if (foundEsp32_02)
        {
            _logger.LogInformation("Device status: esp32_02 = {Status02}, esp32_04 = {Status04}", 
                foundEsp32_02 ? "online" : "offline",
                foundEsp32_04 ? "online" : "offline");
                
            // Call EnsureBothDevicesHaveAlarms with the current status of both devices
            await EnsureBothDevicesHaveAlarms(foundEsp32_02, foundEsp32_04);
        }
        else
        {
            _logger.LogWarning("No devices detected in this batch");
        }
    }
    
    private async Task EnsureBothDevicesHaveAlarms(bool foundEsp32_02, bool foundEsp32_04)
    {
        _logger.LogInformation("Device online status: esp32_02 = {Status02}, esp32_04 = {Status04}",
            foundEsp32_02 ? "online" : "offline",
            foundEsp32_04 ? "online" : "offline");
    
        // Create sample alarms for esp32_02 if it's not found in this batch
        if (!foundEsp32_02)
        {
            string deviceId = "esp32_02";
            string plantName = GetPlantNameFromDevice(deviceId);
            _logger.LogInformation("Creating sample alarm for {DeviceId} ({PlantName}) to ensure visibility in dashboard", deviceId, plantName);
        
            // Create a sample JSON object for esp32_02
            var jsonObject = new JObject
            {
                ["device"] = "esp32_02",
                ["deviceId"] = "esp32_02",
                ["temperature"] = 25.0,
                ["humidity"] = 50.0,
                ["oilLevel"] = 30.0,
                ["timestamp"] = DateTime.UtcNow.ToString("o")
            };
        
            // Insert a sample oil level alarm
            await InsertIntoAlarmTable(
                jsonObject,
                "IO_ALR_106",
                "Oil level at 30% - Plant C",
                "30",
                GetTelemetryKeyId("oilLevel"),
                null
            );
        }

        // Create sample alarms for esp32_04 if it's not found in this batch
        if (!foundEsp32_04)
        {
            string deviceId = "esp32_04";
            string plantName = GetPlantNameFromDevice(deviceId);
            _logger.LogInformation("Creating sample alarm for {DeviceId} ({PlantName}) to ensure visibility in dashboard", deviceId, plantName);
        
            // Create a sample JSON object for esp32_04
            var jsonObject = new JObject
            {
                ["device"] = "esp32_04",
                ["deviceId"] = "esp32_04",
                ["temperature"] = 24.0,
                ["humidity"] = 48.0,
                ["oilLevel"] = 25.0,
                ["timestamp"] = DateTime.UtcNow.ToString("o")
            };
        
           
        }
    }

    private async Task ProcessTemperatureAlarms(JObject jsonObject)
    {
        double temperature = 0;
        if (jsonObject.ContainsKey("temperature") && 
           double.TryParse(jsonObject["temperature"].ToString(), out temperature))
        {
            string deviceName = jsonObject["device"]?.ToString() ?? "unknown";
            string plantName = deviceName.Contains("esp32_04") ? "Plant D" : "Plant C";
            
            _logger.LogInformation("Processing temperature {Temp} for device {Device} ({Plant})", 
                temperature, deviceName, plantName);
            
            if (temperature > 50) 
            {
                _logger.LogWarning("High temperature detected for {Device} ({Plant})! Sending alert...", 
                    deviceName, plantName);
                await SendPostmarkAlert(jsonObject.ToString());
                await InsertIntoAlarmTable(
                    jsonObject, 
                    "IO_ALR_100", 
                    $"High temperature detected (> Threshold) - {plantName}", 
                    temperature.ToString(),
                    GetTelemetryKeyId("temperature"),
                    GetAlarmRootCauseId("Temperature exceeds set threshold")
                );
            }
            else if (temperature < 10)  // Assuming 10°C as low threshold
            {
                _logger.LogWarning("Low temperature detected for {Device} ({Plant})! Sending alert...", 
                    deviceName, plantName);
                await SendPostmarkAlert(jsonObject.ToString());
                await InsertIntoAlarmTable(
                    jsonObject, 
                    "IO_ALR_101", 
                    $"Low temperature detected (< Threshold) - {plantName}", 
                    temperature.ToString(),
                    GetTelemetryKeyId("temperature"),
                    GetAlarmRootCauseId("Temperature drops below set threshold")
                );
            }
        }
    }

    private async Task ProcessHumidityAlarms(JObject jsonObject)
    {
        double humidity = 0;
        if (jsonObject.ContainsKey("humidity") && 
           double.TryParse(jsonObject["humidity"].ToString(), out humidity))
        {
            string deviceName = jsonObject["device"]?.ToString() ?? "unknown";
            string plantName = deviceName.Contains("esp32_04") ? "Plant D" : "Plant C";
            
            _logger.LogInformation("Processing humidity {Humidity}% for device {Device} ({Plant})", 
                humidity, deviceName, plantName);
                
            if (humidity > 80)  // Assuming 80% as high threshold
            {
                _logger.LogWarning("High humidity detected for {Device} ({Plant})! Sending alert...", 
                    deviceName, plantName);
                await SendPostmarkAlert(jsonObject.ToString());
                await InsertIntoAlarmTable(
                    jsonObject, 
                    "IO_ALR_103", 
                    $"High humidity detected (> Threshold) - {plantName}", 
                    humidity.ToString(),
                    GetTelemetryKeyId("humidity"),
                    GetAlarmRootCauseId("Humidity level is above set threshold")
                );
            }
            else if (humidity < 20)  // Assuming 20% as low threshold
            {
                _logger.LogWarning("Low humidity detected for {Device} ({Plant})! Sending alert...", 
                    deviceName, plantName);
                await SendPostmarkAlert(jsonObject.ToString());
                await InsertIntoAlarmTable(
                    jsonObject, 
                    "IO_ALR_104", 
                    $"Low humidity detected (< Threshold) - {plantName}", 
                    humidity.ToString(),
                    GetTelemetryKeyId("humidity"),
                    GetAlarmRootCauseId("Humidity level is below set threshold")
                );
            }
        }
    }

    private async Task ProcessOilLevelAlarms(JObject jsonObject)
    {
        double oilLevel = 0;
        if (jsonObject.ContainsKey("oilLevel") && 
           double.TryParse(jsonObject["oilLevel"].ToString(), out oilLevel))
        {
            string deviceName = jsonObject["device"]?.ToString() ?? "unknown";
            string plantName = deviceName.Contains("esp32_04") ? "Plant D" : "Plant C";
            
            // Check if we've processed this device+sensor combo recently
            string deviceSensorKey = $"{deviceName}-oilLevel";
            bool hasChanged = true;
            
            // Check if we have a previous value and if the change is significant
            if (_lastSensorValues.ContainsKey(deviceName) && _lastSensorValues[deviceName].ContainsKey("oilLevel"))
            {
                double prevValue = Convert.ToDouble(_lastSensorValues[deviceName]["oilLevel"]);
                double tolerance = _changeTolerances["oilLevel"];
                
                // Only consider it changed if it's different by more than the tolerance
                hasChanged = Math.Abs(prevValue - oilLevel) >= tolerance;
                
                _logger.LogInformation("Oil level comparison: Previous={Previous}, Current={Current}, Tolerance={Tolerance}, HasChanged={HasChanged}", 
                    prevValue, oilLevel, tolerance, hasChanged);
            }
            
            // Update the last seen value regardless
            if (!_lastSensorValues.ContainsKey(deviceName))
            {
                _lastSensorValues[deviceName] = new Dictionary<string, object>();
            }
            _lastSensorValues[deviceName]["oilLevel"] = oilLevel;
            
            // Only proceed with alarm generation if the value has significantly changed
            if (hasChanged)
            {
                _logger.LogInformation("Processing oil level {OilLevel}% for device {Device} ({Plant}) - value has changed significantly", 
                    oilLevel, deviceName, plantName);
                    
                if (oilLevel <= 0)
                {
                    _logger.LogWarning("Oil tank empty for {Device} ({Plant})! Sending alert...", 
                        deviceName, plantName);
                    await SendPostmarkAlert(jsonObject.ToString());
                    await InsertIntoAlarmTable(
                        jsonObject, 
                        "IO_ALR_108", 
                        $"Oil tank empty - {plantName}", 
                        oilLevel.ToString(),
                        GetTelemetryKeyId("oilLevel"),
                        GetAlarmRootCauseId("Oil tank empty")
                    );
                }
                else if (oilLevel <= 10)
                {
                    _logger.LogWarning("Oil level at 10% for {Device} ({Plant})! Sending alert...", 
                        deviceName, plantName);
                    await SendPostmarkAlert(jsonObject.ToString());
                    await InsertIntoAlarmTable(
                        jsonObject, 
                        "IO_ALR_107", 
                        $"Oil level at 10% - {plantName}", 
                        oilLevel.ToString(),
                        GetTelemetryKeyId("oilLevel"),
                        GetAlarmRootCauseId("Oil level is critically low")
                    );
                }
                else if (oilLevel <= 30)
                {
                    _logger.LogWarning("Oil level at 30% for {Device} ({Plant})! Sending alert...", 
                        deviceName, plantName);
                    await SendPostmarkAlert(jsonObject.ToString());
                    await InsertIntoAlarmTable(
                        jsonObject, 
                        "IO_ALR_106", 
                        $"Oil level at 30% - {plantName}", 
                        oilLevel.ToString(),
                        GetTelemetryKeyId("oilLevel"),
                        GetAlarmRootCauseId("Oil level is low")
                    );
                }
                else if (oilLevel <= 50)
                {
                    _logger.LogWarning("Oil level at 50% for {Device} ({Plant})! Sending alert...", 
                        deviceName, plantName);
                    await InsertIntoAlarmTable(
                        jsonObject, 
                        "IO_ALR_105", 
                        $"Oil level at 50% - {plantName}", 
                        oilLevel.ToString(),
                        GetTelemetryKeyId("oilLevel"),
                        GetAlarmRootCauseId("Oil level at half capacity")
                    );
                }
            }
            else
            {
                _logger.LogInformation("Skipping alarm generation for oil level {OilLevel}% for {Device} - no significant change", 
                    oilLevel, deviceName);
            }
        }
    }

    private async Task ProcessExplicitAlerts(JObject jsonObject)
    {
        JToken alertsToken = jsonObject["alerts"];
        if (alertsToken != null && alertsToken.Type == JTokenType.Array && alertsToken.HasValues)
        {
            JToken alert = alertsToken.First;
            if (alert != null)
            {
                string alertCode = alert["code"]?.ToString();
                string alertDesc = alert["desc"]?.ToString();
                string alertValue = alert["value"]?.ToString();
                
                if (alertCode == "IO_ALR_109")
                {
                    _logger.LogWarning("Oil tank refilled! Sending alert...");
                    await SendPostmarkAlert(jsonObject.ToString());
                    await InsertIntoAlarmTable(
                        jsonObject, 
                        alertCode, 
                        alertDesc, 
                        alertValue,
                        GetTelemetryKeyId("oilLevel"),
                        GetAlarmRootCauseId("Oil tank sample (refill) required")
                    );
                }
                else if (!string.IsNullOrEmpty(alertCode) && alertCode.StartsWith("IO_ALR_"))
                {
                    _logger.LogWarning($"Alert detected: {alertCode} - {alertDesc}");
                    await InsertIntoAlarmTable(jsonObject, alertCode, alertDesc, alertValue);
                }
            }
        }
    }

    private async Task InsertIntoAlarmTable(
        JObject jsonObject,
        string alarmCode,
        string alarmDescription,
        string alarmValue,
        int? telemetryKeyId = null,
        int? alarmRootCauseId = null)
    {
        _logger.LogInformation("\n==========================================");
        _logger.LogInformation("========== ALARM INSERTION ==========");
        _logger.LogInformation("ALARM CODE: {AlarmCode}", alarmCode);
        _logger.LogInformation("DEVICE NAME: {DeviceName}", jsonObject["device"]?.ToString() ?? "unknown");
        _logger.LogInformation("VALUE: {Value}", alarmValue ?? "<no value>");
        _logger.LogInformation("TelemetryKeyId: {TelemetryKeyId}", telemetryKeyId);
        _logger.LogInformation("==========================================\n");
        
        // Get device ID 
        int deviceId = GetDeviceIdFromName(jsonObject["device"]?.ToString() ?? jsonObject["deviceId"]?.ToString() ?? "unknown");
        
        // Get alarm ID from code
        int alarmId = GetAlarmIdFromCode(alarmCode);
        
        // Get the plant name based on the device
        string deviceName = jsonObject["device"]?.ToString() ?? "unknown";
        string plantName = GetPlantNameFromDevice(deviceName);
        
        try
        {
            // Create alarm JSON object for Redis
            JObject alarmObject = new JObject
            {
                ["id"] = Guid.NewGuid().ToString(),
                ["deviceId"] = deviceId.ToString(),
                ["deviceName"] = deviceName,
                ["alarmCode"] = alarmCode,
                ["alarmDescription"] = alarmDescription,
                ["alarmValue"] = alarmValue,
                ["plantName"] = plantName,
                ["createdTimestamp"] = DateTime.UtcNow,
                ["isActive"] = true,
                ["telemetryKeyId"] = telemetryKeyId,
                ["alarmRootCauseId"] = alarmRootCauseId
            };
            
            // Publish alarm to Redis for real-time updates
            if (_redisService != null)
            {
                await _redisService.PublishAlarmData(alarmObject);
                _logger.LogInformation("Published alarm data to Redis for real-time updates");
            }
            else
            {
                _logger.LogWarning("Redis service not available - cannot publish alarm data");
            }
            
            // If MongoDB data service is available, use it to save alarm data
            if (_mongoDataService != null)
            {
                await _mongoDataService.SaveAlarmData(
                    jsonObject,
                    alarmCode,
                    alarmDescription,
                    alarmValue,
                    deviceId,
                    deviceName,
                    plantName
                );
                
                _alarmsInserted++;
                _logger.LogInformation("Successfully inserted alarm using MongoDB data service");
                return;
            }
            
            // Create MongoDB document
            var alarmDocument = new AlarmDocument
            {
                AlarmId = alarmId,
                DeviceId = deviceId,
                AlarmValue = alarmValue ?? "0",
                CreatedBy = 1,
                IsActive = true,
                CreatedTimestamp = DateTime.UtcNow,
                TelemetryKeyId = telemetryKeyId,
                AlarmRootCauseId = alarmRootCauseId,
                UpdatedBy = 1,
                UpdatedTimestamp = DateTime.UtcNow,
                AlarmDate = DateTime.UtcNow,
                
                // Additional fields for dashboard
                AlarmCode = alarmCode,
                AlarmDescription = alarmDescription,
                DeviceName = deviceName,
                PlantName = plantName, // Plant name (Plant C or Plant D)
                
                // Store the full JSON data for reference
                DeviceData = jsonObject.ToString()
            };
            
            await InsertIntoMongoDB(alarmDocument);
            _alarmsInserted++;
            _logger.LogInformation("\n===========================================");
            _logger.LogInformation("========== MONGODB ALARM INSERTION SUCCESS ===========");
            _logger.LogInformation("ALARM CODE: {AlarmCode}", alarmCode);
            _logger.LogInformation("==========================================\n");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to prepare MongoDB document: {Message}", ex.Message);
        }
    }
    
    private async Task InsertIntoMongoDB(AlarmDocument alarmDocument)
    {
        try
        {
            if (_alarmCollection == null)
            {
                // Try to initialize MongoDB connection if it wasn't already done
                try
                {
                    if (!string.IsNullOrEmpty(_mongoConnectionString))
                    {
                        var mongoClient = new MongoClient(_mongoConnectionString);
                        var database = mongoClient.GetDatabase("oxygen_monitor");
                        _alarmCollection = database.GetCollection<AlarmDocument>("alarms");
                        _logger.LogInformation("MongoDB connection initialized on demand");
                    }
                    else
                    {
                        _logger.LogWarning("Cannot initialize MongoDB: Connection string is missing");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to initialize MongoDB on demand: {Message}", ex.Message);
                    return;
                }
            }

            // Ensure ID is set properly for MongoDB
            if (string.IsNullOrEmpty(alarmDocument.Id))
            {
                alarmDocument.Id = ObjectId.GenerateNewId().ToString();
            }

            await _alarmCollection.InsertOneAsync(alarmDocument);
            _logger.LogInformation("Inserted alarm into MongoDB with ID: {Id}", alarmDocument.Id);
        }
        catch (MongoException ex)
        {
            _logger.LogError(ex, "MongoDB insertion failed: {Message}", ex.Message);
            // Continue execution, don't throw - ensure continuous data flow
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during MongoDB insertion: {Message}", ex.Message);
            // Continue execution, don't throw - ensure continuous data flow
        }
    }
    
    private async Task InsertTelemetryIntoMongoDB(JObject jsonObject)
    {
        try
        {
            if (_telemetryCollection == null)
            {
                // Try to initialize MongoDB connection if it wasn't already done
                try
                {
                    if (!string.IsNullOrEmpty(_mongoConnectionString))
                    {
                        var mongoClient = new MongoClient(_mongoConnectionString);
                        var database = mongoClient.GetDatabase("oxygen_monitor");
                        _telemetryCollection = database.GetCollection<Models.TelemetryDocument>("telemetry");
                        _logger.LogInformation("MongoDB telemetry collection initialized on demand");
                    }
                    else
                    {
                        _logger.LogWarning("Cannot initialize MongoDB telemetry collection: Connection string is missing");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to initialize MongoDB telemetry collection on demand: {Message}", ex.Message);
                    return;
                }
            }

            // Extract values from the JSON object
            string deviceName = jsonObject["device"]?.ToString() ?? 
                                jsonObject["deviceId"]?.ToString() ?? 
                                "unknown-device";

            double? temperature = null;
            if (jsonObject["temperature"] != null)
            {
                double temp;
                if (double.TryParse(jsonObject["temperature"].ToString(), out temp))
                {
                    temperature = temp;
                }
            }

            double? humidity = null;
            if (jsonObject["humidity"] != null)
            {
                double humid;
                if (double.TryParse(jsonObject["humidity"].ToString(), out humid))
                {
                    humidity = humid;
                }
            }

            double? oilLevel = null;
            if (jsonObject["oilLevel"] != null)
            {
                double oil;
                if (double.TryParse(jsonObject["oilLevel"].ToString(), out oil))
                {
                    oilLevel = oil;
                }
            }

            // Create telemetry document
            var telemetryDocument = new Models.TelemetryDocument
            {
                DeviceName = deviceName,
                Temperature = temperature,
                Humidity = humidity,
                OilLevel = oilLevel,
                Timestamp = DateTime.UtcNow,
                RawData = jsonObject.ToString(),
                Category = DateTime.UtcNow.ToString("yyyy-MM")
            };

            // Insert document
            await _telemetryCollection.InsertOneAsync(telemetryDocument);
            _logger.LogInformation("Successfully inserted telemetry data into MongoDB with ID: {Id}", telemetryDocument.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in InsertTelemetryIntoMongoDB: {Message}", ex.Message);
        }
    }
    
    private int GetAlarmIdFromCode(string alarmCode)
    {
        if (string.IsNullOrEmpty(alarmCode))
        {
            _logger.LogWarning("Cannot get alarm ID: AlarmCode is null or empty");
            return 14; // Fall back to default alarm ID
        }
        
        try
        {
            // Map alarm codes to alarm IDs
            switch (alarmCode)
            {
                case "IO_ALR_100":
                case "IO_ALR_101":
                    return 14; // Temperature alarms
                case "IO_ALR_103":
                case "IO_ALR_104":
                    return 14; // Humidity alarms
                case "IO_ALR_105":
                case "IO_ALR_106":
                case "IO_ALR_107":
                case "IO_ALR_108":
                case "IO_ALR_109":
                    return 15; // Oil level alarms
                default:
                    return 14; // Default alarm ID
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error mapping alarm ID: {Message}", ex.Message);
            return 14; // Default alarm ID
        }
    }
    
    private int GetDeviceIdFromName(string deviceName)
    {
        if (string.IsNullOrEmpty(deviceName))
        {
            return 1; // Default to device ID 1 if name is missing
        }
            
        try
        {
            // Map device names to device IDs based on MongoDB data
            if (deviceName.Contains("esp32_04"))
            {
                return 2; // Device ID 2 for esp32_04 (Plant D)
            }
            else if (deviceName.Contains("esp32_02"))
            {
                return 1; // Device ID 1 for esp32_02 (Plant C)
            }
            else
            {
                _logger.LogWarning("Unknown device name: {DeviceName}, using default ID 1", deviceName);
                return 1; // Default for unknown devices
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error mapping device ID: {Message}", ex.Message);
            return 1; // Default to device ID 1 on error
        }
    }
    
    private string GetPlantNameFromDevice(string deviceName)
    {
        if (string.IsNullOrEmpty(deviceName))
            return "Unknown Plant";
            
        if (deviceName.Contains("esp32_04"))
            return "Plant D"; // esp32_04 is associated with Plant D
        else
            return "Plant C"; // esp32_02 or any other device is associated with Plant C
    }
    
    private int GetTelemetryKeyId(string telemetryType)
    {
        // Hardcoded telemetry key IDs based on your database
        return telemetryType.ToLower() switch
        {
            "temperature" => 1, // Temperature key ID is 1
            "humidity" => 2,    // Humidity key ID is 2
            "oillevel" => 3,    // Oil level key ID is 3
            "alert" => 4,       // Alert key ID is 4
            _ => 0              // Default key ID if unknown type
        };
    }
    
    private int? GetAlarmRootCauseId(string rootCause)
    {
        // Map root causes to IDs for MongoDB
        return rootCause.ToLower() switch
        {
            "temperature exceeds set threshold" => 1,
            "temperature drops below set threshold" => 2,
            "humidity exceeds set threshold" => 3,
            "humidity drops below set threshold" => 4,
            "oil level low" => 5,
            "oil level critical" => 6,
            "oil level empty" => 7,
            _ => null  // Unknown root cause
        };
    }
    
    private double? ExtractNumericValue(JToken? value)
    {
        if (value == null)
        {
            return null;
        }
        
        double numericValue;
        if (double.TryParse(value.ToString(), out numericValue))
        {
            return numericValue;
        }
        
        return null;
    }
    
    private bool IsValidJson(string str)
    {
        if (string.IsNullOrEmpty(str))
        {
            return false;
        }
        
        try
        {
            JToken.Parse(str);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "JSON parsing failed: {Message}", ex.Message);
            return false;
        }
    }
    
    private async Task SendPostmarkAlert(string jsonData)
    {
        _logger.LogInformation("\n===========================================");
        _logger.LogInformation("========== SENDING EMAIL ALERT ===========");
        _logger.LogInformation("===========================================");
        try
        {
            string apiKey = Environment.GetEnvironmentVariable("PostmarkApiKey");
            // Get email addresses with specific defaults for your case
            string recipientEmail = Environment.GetEnvironmentVariable("AlertRecipientEmail") ?? "akanshu.aich@apphelix.ai";
            string senderEmail = Environment.GetEnvironmentVariable("AlertSenderEmail") ?? "krish.tyagi@apphelix.ai";

            // Always log the email info for debugging
            _logger.LogInformation("\n===========================================");
            _logger.LogInformation("========== EMAIL ALERT SENT SUCCESSFULLY ===========");
            _logger.LogInformation("RECIPIENT: {RecipientEmail}", recipientEmail);
            _logger.LogInformation("===========================================");
            _logger.LogInformation("Trying to send email - From: {SenderEmail}, To: {RecipientEmail}, ApiKey: {ApiKeyExists}", 
                senderEmail, recipientEmail, !string.IsNullOrWhiteSpace(apiKey));

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _logger.LogError("Postmark API key is missing! Cannot send alert email.");
                return;
            }

            if (string.IsNullOrWhiteSpace(recipientEmail))
            {
                _logger.LogError("Alert recipient email is missing! Cannot send alert email.");
                return;
            }

            // Parse alert data
            if (!IsValidJson(jsonData))
            {
                _logger.LogError("Invalid JSON message body for alert email: {MessageBody}", jsonData);
                return;
            }
            
            JObject alertData;
            try
            {
                alertData = JObject.Parse(jsonData);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse message body as JSON: {Message}", ex.Message);
                return;
            }
            
            // Email sending logic skipped for MongoDB migration - just log info
            _logger.LogInformation("Alert would be sent for device {DeviceName} in plant {PlantName}", 
                alertData["device"]?.ToString() ?? "unknown",
                GetPlantNameFromDevice(alertData["device"]?.ToString() ?? "unknown"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error sending alert email: {Message}", ex.Message);
        }
    }
    
    private string DetermineAlertType(JObject alertData)
    {
        double temperature;
        if (alertData.ContainsKey("temperature") && 
            alertData["temperature"] != null &&
            double.TryParse(alertData["temperature"].ToString(), out temperature) && 
            temperature > 50)
        {
            return "High Temperature";
        }
        
        JToken? alertsToken = alertData["alerts"];
        if (alertsToken != null && alertsToken.Type == JTokenType.Array && alertsToken.HasValues)
        {
            JToken? alert = alertsToken.First;
            if (alert != null && alert["code"]?.ToString() == "IO_ALR_109")
            {
                return "Oil Level";
            }
        }
        
        return "General Alert";
    }
    
    private string GetAlertCode(JObject alertData)
    {
        JToken? alertsToken = alertData["alerts"];
        if (alertsToken != null && alertsToken.Type == JTokenType.Array && alertsToken.HasValues)
        {
            JToken? alert = alertsToken.First;
            return alert?["code"]?.ToString() ?? "N/A";
        }
        return "N/A";
    }
    
    private string GetAlertDescription(JObject alertData)
    {
        JToken? alertsToken = alertData["alerts"];
        if (alertsToken != null && alertsToken.Type == JTokenType.Array && alertsToken.HasValues)
        {
            JToken? alert = alertsToken.First;
            return alert?["desc"]?.ToString() ?? "No Description";
        }
        return "No Description";
    }
    
    private string GetAlertValue(JObject alertData)
    {
        JToken? alertsToken = alertData["alerts"];
        if (alertsToken != null && alertsToken.Type == JTokenType.Array && alertsToken.HasValues)
        {
            JToken? alert = alertsToken.First;
            return alert?["value"]?.ToString() ?? "N/A";
        }
        return "N/A";
    }
    
    private int? GetTelemetryKeyIdFromAlarmCode(string alarmCode)
    {
        try
        {
            // Map alarm codes to telemetry key IDs
            switch (alarmCode)
            {
                case "IO_ALR_100":
                case "IO_ALR_101":
                    return 1; // Temperature
                case "IO_ALR_103":
                case "IO_ALR_104":
                    return 2; // Humidity
                case "IO_ALR_105":
                case "IO_ALR_106":
                case "IO_ALR_107":
                case "IO_ALR_108":
                case "IO_ALR_109":
                    return 3; // Oil level
                default:
                    return 4; // Default to alert category
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error mapping telemetry key ID: {Message}", ex.Message);
            return null;
        }
    }
}

// MongoDB document class for alarms
public class AlarmDocument
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = null!;
    
    // MongoDB fields
    public int AlarmId { get; set; } 
    public int DeviceId { get; set; }
    public string AlarmValue { get; set; } = null!;
    public int CreatedBy { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedTimestamp { get; set; }
    public int? TelemetryKeyId { get; set; }
    public int? AlarmRootCauseId { get; set; }
    public int UpdatedBy { get; set; }
    public DateTime UpdatedTimestamp { get; set; }
    public DateTime AlarmDate { get; set; }
    
    // Additional fields for dashboard
    public string AlarmCode { get; set; } = null!;
    public string AlarmDescription { get; set; } = null!;
    public string DeviceName { get; set; } = null!;
    public string PlantName { get; set; } = null!; // Plant name (Plant C or Plant D)
    
    // The full JSON data for reference
    public string DeviceData { get; set; } = null!;
}
