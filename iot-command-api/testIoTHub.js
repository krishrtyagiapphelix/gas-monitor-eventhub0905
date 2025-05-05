require("dotenv").config();
const iothub = require("azure-iothub");

const connectionString = process.env.AZURE_IOT_HUB_CONN_STRING;
const registry = iothub.Registry.fromConnectionString(connectionString);

async function checkIoTHub() {
    try {
        console.log("🔹 Checking IoT Hub connection...");
        const result = await registry.list();
        console.log("✅ IoT Hub Connection Successful. Found devices:", result);
    } catch (error) {
        console.error("❌ IoT Hub Connection Failed:", error.message);
    }
}

checkIoTHub();
