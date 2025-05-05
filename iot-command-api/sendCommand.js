require("dotenv").config();
// Import the correct packages for Azure IoT Hub
const Client = require('azure-iothub').Client;
const { Message } = require('azure-iot-common');

const connectionString = process.env.AZURE_IOT_HUB_CONN_STRING;
if (!connectionString) {
    console.error("ERROR: AZURE_IOT_HUB_CONN_STRING is missing in .env file");
    process.exit(1);
}

const serviceClient = Client.fromConnectionString(connectionString);

async function sendCloudToDeviceMessage(deviceId, command, value = null) {
    console.log("Connecting to Azure IoT Hub...");

    try {
        console.log("Inside try block");

        const messageData = {
            command,
            value,
            timestamp: new Date().toISOString(),
        };

        console.log("Message payload created:", JSON.stringify(messageData));

        const message = new Message(JSON.stringify(messageData));
        console.log("Message object created");

        await serviceClient.open();
        console.log("IoT Hub connection opened.");

        await serviceClient.send(deviceId, message);
        console.log(`Command sent successfully to '${deviceId}'`);
        
        return true;
    } catch (err) {
        console.error("ERROR Sending Command:", err.message);
        throw err; // Re-throw to be caught by the API
    } finally {
        try {
            await serviceClient.close();
            console.log("IoT Hub connection closed.");
        } catch (closeErr) {
            console.error("ERROR closing connection:", closeErr.message);
        }
    }
}

module.exports = { sendCloudToDeviceMessage };