# Mi Band 10 Controller for Notifications on the Tatum1 Robot

A standalone application for controlling Xiaomi Mi Band devices via Bluetooth SPP. This program connects to a Mi Band 10, authenticates, and exposes an HTTP API for triggering vibration patterns. It uses code from and is based on the [AstroBox repo](https://github.com/AstralSightStudios/AstroBox-NG). They documented the Xiaomi protocol and provide the library that this program uses to connect/authenticate/communicate with the watch.

# What does the code do?

1. It scans for and connects to the Mi Band with Bluetooth
2. Authenticates using a token from the Mi Fitness app
3. Has a server with endpoints for triggering custom vibration patterns
4. Attempts reconnection every 30 seconds on disconnect

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

## Configuring the code

In the config.yml file in the main folder, there are a few settings that must be configured for each watch before running the code.

```yaml
device:
  name: "Xiaomi Smart Band 10 8DCA" # the name of the watch may be different from this, but check what it appears as in the Bluetooth connections menu on the phone/computer
  mac_address: "04:34:C3:A4:8D:CA"  # paste in the watch's MAC address, found in the Settings, in the About section
  auth_key: "13b6840ba233108fd714cbae5f7a3346"  # paste in the token from the official Mi Fitness app
  ```

Adding custom vibration patterns can be done by changing the patterns field in the config file. Here's an example one with two quick pulses.

```yaml
patterns:
  example_pattern:
  - on: true
    duration: 100
    strength: 100
  - on: false
    duration: 100
    strength: 0
  - on: true
    duration: 100
    strength: 100
```

The port and web server route name can be changed in the config as well!

```yaml
web_server:
  port: 3000
  vibration_route_name: "/vibrate"
```

## Building and Running

### Initial Setup

1. [Install Rust](https://rust-lang.org/tools/install/)
2. From the main folder, initialize AstroBox's code:
```bash
python abtools.py init
```
3. Add the btclassic-ssp module. In the src-tauri/modules folder, run
```bash
git clone https://github.com/AstralSightStudios/AstroBox-NG-Plugin-BtClassicSpp.git
```
4. If you have gone through all of the authentication setup, the watch is probably still connected to the Android phone. In that case, go to Settings, System, then press "Connect new phone". On this new screen (there should be a QR code), you must press "Pair" if it pops up while trying to connect to the computer. On Windows, after pressing pair, you usually have to allow the connection when the request comes up. It'll say something like "Pair Device? [Watch Name] would like to pair with this Windows device. Do you want to allow this?" and you have to press "Allow".
5. Compile and run the code

```bash
cargo run -p mi_band_controller --manifest-path src-tauri/Cargo.toml
```

5. Set up some sort of linux service or other method of restarting the app when it shuts down. This is because it reconnects to the watch by shutting down after 30 seconds (the number of seconds is configurable in config.yml). It expects to be restarted externally. It isn't ideal, but restarting within the same proccess might be a bit tricky.

## The Code

### device.rs
- Bluetooth SPP connection using the `btclassic-spp` plugin
- Authenticates devices with AstroBox's code `corelib`
- Sends packets to the Mi Band

### main.rs
- Initializes logging and loads configuration
- Connects to the device and authenticates
- Starts HTTP server and connection monitor
- Shuts down program on disconnect

### server.rs
- POST endpoint `/vibrate` accepts JSON: `{"pattern": "heartbeat"}` or any of the other patterns defined in the config

## API Usage

Start the controller, then trigger vibrations via HTTP:

```bash
curl -X POST http://localhost:3000/vibrate \
  -H "Content-Type: application/json" \
  -d '{"pattern": "heartbeat"}'
```
