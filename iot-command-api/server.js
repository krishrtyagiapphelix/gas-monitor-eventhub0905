require("dotenv").config();
const express = require("express");
const cors = require("cors");
const { sendCloudToDeviceMessage } = require("./sendCommand");

const app = express();

app.use(cors({
  origin: '*', // Allow all origins
  methods: ['GET', 'POST', 'PUT', 'DELETE'],
  allowedHeaders: ['Content-Type', 'Authorization']
}));
app.use(express.json());

const PORT = process.env.PORT || 3000;

// Handle unhandled promise rejections globally
process.on("unhandledRejection", (reason, promise) => {
    console.error("Unhandled Rejection:", reason);
});

app.listen(PORT, () => {
    console.log(`Server running on http://localhost:${PORT}`);
});

app.post("/api/device/command", async (req, res) => {
    console.log("Received API Request:", req.body);

    const { deviceName, command, value } = req.body;

    if (!deviceName || !command) {
        console.error("ERROR: Missing deviceName or command");
        return res.status(400).json({ error: "Device name and command are required" });
    }

    try {
        console.log(`Sending command '${command}' to device '${deviceName}' with value '${value}'`);
        await sendCloudToDeviceMessage(deviceName, command, value);
        console.log(`Command '${command}' sent successfully to '${deviceName}'`);
        res.status(200).json({ message: `Command '${command}' sent to device '${deviceName}'` });
    } catch (err) {
        console.error("ERROR in API:", err);
        res.status(500).json({ error: `Failed to send command: ${err.message}` });
    }
});