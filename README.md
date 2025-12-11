# Mi Band 10 Controller for Notifications on the Tatum1 Robot
A standalone application for controlling Xiaomi Mi Band devices via Bluetooth SPP on a Raspberry Pi.
This program connects to a Mi Band 10, authenticates, and can send vibration patterns or fetch the device's battery/sensor data.
It's in C#, but was ported from the Rust [AstroBox repo](https://github.com/AstralSightStudios/AstroBox-NG).
They documented the Xiaomi protocol and provide a library to connect/authenticate/communicate with the watch.

The code was ported from the following AstroBox modules developed by AstralSight Studios under the GNU Affero General Public License at the time of writing:
- The core (authentication and connection) - [core](https://github.com/AstralSightStudios/AstroBox-NG-Module-Core)
- The Google ProtoBuf protocol files - [pb](https://github.com/AstralSightStudios/AstroBox-NG-Module-Pb)
- Bluetooth - [btclassic-spp](https://github.com/AstralSightStudios/AstroBox-NG-Plugin-BtClassicSpp)

## The C# code depends on a few NuGet packages:
- [Google ProtoBuf](https://protobuf.dev/)
- Microsoft's Logging
- Desktop Bus (DBus) for bluetooth connection through BlueZ on Linux
- YamlDotNet to parse the config file

## Getting the authentication token

1. Install the official Mi Fitness app on an Android phone
2. Sign in and connect to the Mi Band
3. Go to the phone's Bluetooth settings
4. Tap on the Mi Band and press "Unpair" in the bottom right
5. Connect the phone to any computer via USB
6. In file explorer, navigate to:
   
   [Phone Name]\Phone\Android\data\com.xiaomi.wearable\files\log\
   
   Replace [Phone Name] with the phone's name like Galaxy S9+
7. Copy XiaomiFit.main.log from the phone to your computer
8. Open the file in a text editor and search for `"token":`
9. Copy the token value (it should look something like: `13b6840ba233108fd714cbae5f7a3346`)
10. The watch is probably still connected to the Android phone. In that case, disconnect the watch from the Android phone's bluetooth settings. Then, go to the watch's Settings -> System -> "Connect new phone". On this new screen (there should be a QR code, don't scan it), you must press "Pair" if it pops up while trying to connect to the PI.

## Configuring the code

In the config.yml file in the main folder, there are a few settings that must be configured for each watch before running the code.

```yaml
device:
  name: "Xiaomi Smart Band 10 8DCA" # the name of the watch may be different from this, but check what it appears as in the Bluetooth connections menu on the phone/computer
  mac_address: "04:34:C3:A4:8D:CA"  # paste in the watch's MAC address, found in the watch's Settings, in the About section
  auth_key: "13b6840ba233108fd714cbae5f7a3346"  # paste in the token from the folder of the official Mi Fitness app
  ```

Adding custom vibration patterns can be done by changing the patterns field in the config file. Here's an example one with two quick pulses.
The durations are in milliseconds and the strengths are from 0 to 100.

```yaml
patterns:
  example_pattern:
  - on: true
    duration: 100
    strength: 100
  - on: false
    duration: 100
  - on: true
    duration: 100
    strength: 100
```

## Building and using the app
Run this to build it for the pi:
```bash
dotnet publish -c Release -r linux-arm64
```
The executable will be `\bin\Release\net8.0\linux-arm64\publish\XiaomiAstroBoxCSharp`.
Make sure the config.yml file is in the same directory as the executable when you run it, or specify its file location as the first command line argument.
To test the app, make sure you have the bluetooth group on Linux:
```bash
sudo usermod -aG bluetooth your_user
```
then restart the Pi.
In the bluetoothctl, scan for the watch (make sure you see the QR code. If you don't, go into Settings -> System -> Connect new phone).
To do that, first run the `bluetoothctl` command and enter in these into the [bluetooth]:
```bash
power on
scan on
```
Then, wait until you see the Xiaomi watch. It can take a minute! It should print the MAC address and the full name of the watch which should match the ones in your config!
You safe to run `exit` to exit out of the bluetooth command line now.
Run the C# app (but make sure it's an executable):
```bash
chmod +x ./XiaomiAstroBoxCSharp
```
Long term, you need to setup the Linux service to keep it going on restart and on shutdown (such as if the watch disconnects or the user walks too far away from the Pi):
```bash
sudo nano /etc/systemd/system/mi_band_controller.service
```
Then paste in the `mi_band_controller.service` in this repo.
```bash
sudo systemctl enable mi_band_controller
sudo systemctl start mi_band_controller
```
But right now the only way to trigger it is by command line and typing in commands.
You can enter in the following commands:
- "battery" to fetch the battery %
- "wearing" to fetch if the user is wearing the watch
- Any of the names of the patterns listed in config.yml


## Bluetooth Communication
- Uses the RPI's Bluetooth Classic through BlueZ
    - It know the MAC address from the config
	- It pairs with the device
	- Trusts it so it doesn't have to pair again
- For networking, it uses both the Transport layer (L1) and Application Layer (L2)
	- Transport layer
	- Xiaomi has a header of 0xA5A5 for syncing
	- Has sequence numbers to track packets (first packet is seq #1, second is #2, etc.)
	- Acknowledges (ACK) packets by sending a message
	- Error detection
- Application Layer
	- Uses AES-128-CTR encryption (look at the L2Cipher class in the CryptographyHelper.cs file)
	- Has "WearPacket" data
- The data flow:
	1. Google's Protobuf data structures
	2. Application (L2) layer encryption
	3. Transport (L1) layer sequencing, framing, gaurenteeing of packets
	4. Bluetooth SPP raw bytes
- The authentication code flow:
    1. L1 handshake to communicate config like packet size, timeouts, etc.
	2. Sends encryption challenge of 16 random bytes, which Xiaomi expects for security 
	3. Sends auth key found earlier from the official Mi Fitness app
	4. Creates a cipher for the L2 layer so that it can send encypted WearPackets going forward
- Packets:
	- Uses Google's Protobuf to create packets and define their structure
	- Uses AstroBox's reverse engineered .proto files to know that packet structure


## Future things to do
- Integrate it with the rest of trManager and the rest of the code
- Improve the patterns and define ones for calling, messaging, etc.
- Set the system time on the watch to the actual time with the correct time zone
- Improve reconnection and make it reconnect within the same process. Right now, it has to shut down before it can reconnect. The Linux service is meant to restart it after a delay.