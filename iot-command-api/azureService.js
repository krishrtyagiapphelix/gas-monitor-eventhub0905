require("dotenv").config();
const { Client } = require("azure-iothub");

const connectionString = process.env.AZURE_IOT_HUB_CONN_STRING;
const client = Client.fromConnectionString(connectionString);

async function sendCommandToDevice(deviceName, command, value) {
    if (!deviceName || !command) {
        throw new Error("Device name and command are required.");
    }

    console.log(`Sending command: ${command} to ${deviceName}`);

    const methodParams = {
        methodName: command,
        payload: value ? { threshold: value } : {},
        responseTimeoutInSeconds: 10,
    };

    try {
        const result = await client.invokeDeviceMethod(deviceName, methodParams);
        console.log("Device Response:", result);
        return result;
    } catch (error) {
        console.error("Error sending command:", error);
        throw error;
    }
}

module.exports = { sendCommandToDevice };
