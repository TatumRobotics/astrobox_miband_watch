use anyhow::Result;
use corelib::device::xiaomi::r#type::ConnectType;
use std::sync::Arc;
use std::time::Duration;
use tokio::sync::mpsc;

mod config;
mod device;
mod server;

use config::Config;
use server::AppState;

const EXIT_CODE_CONNECTION_FAILED: i32 = 1;
const EXIT_CODE_AUTH_FAILED: i32 = 2;

#[tokio::main]
async fn main() -> Result<()> {
    // logging
    env_logger::Builder::from_default_env()
        .filter_level(log::LevelFilter::Info)
        .filter_module("corelib", log::LevelFilter::Warn)
        .filter_module("btclassic_spp", log::LevelFilter::Warn)
        .init();

    // load config
    let config = Config::load("config.yml")?;
    log::info!("Loaded config");

    let retry_interval = config.retry_interval_seconds;
    let device_addr = config.device.mac_address.clone();

    // configure device from config
    let band = device::XiaomiBand {
        device_name: config.device.name,
        device_addr: config.device.mac_address,
        auth_key: config.device.auth_key,
        sar_version: config.device.sar_version,
        connect_type: ConnectType::SPP,
        force_android: true,
    };

    let exit_after_delay = move |message: String, exit_code: i32| async move {
        log::error!("{} Restarting program in {} seconds...", message, retry_interval);
        tokio::time::sleep(Duration::from_secs(retry_interval)).await;
        std::process::exit(exit_code);
    };

    corelib::init();

    let (disconnect_tx, mut disconnect_rx) = mpsc::unbounded_channel::<()>();
    
    // connect to device
    if let Err(e) = device::connect_to_device(&device_addr).await {
        exit_after_delay(format!("Failed to connect to device: {}.", e), EXIT_CODE_CONNECTION_FAILED).await;
    }
    
    // set data listener and start subscription (order is important on Linux)
    device::set_data_listener_and_start_subscription(device_addr.clone(), disconnect_tx)?;

    // wait to pair first
    tokio::time::sleep(Duration::from_millis(config.device.connection_delay_ms.unwrap_or(10000))).await;

    // create and authenticate the device
    match device::create_device(band).await {
        Ok(_conn_info) => {
            // set up web server to trigger vibrations on the watch
            let app_state = AppState {
                device_addr,
                patterns: Arc::new(config.patterns),
            };

            let route = config.web_server.vibration_route_name;
            let port = config.web_server.port;

            let (listener, app) = server::start_server(app_state, route, port).await?;
            
            let server = axum::serve(listener, app);
            let server_handle = server.into_future();

            tokio::select! {
                result = server_handle => {
                    if let Err(e) = result {
                        log::error!("Server error: {}", e);
                    }
                }
                _ = disconnect_rx.recv() => {
                    exit_after_delay("Device disconnected.".to_string(), EXIT_CODE_CONNECTION_FAILED).await;
                }
            }
        }
        Err(e) => {
            exit_after_delay(format!("Failed to authenticate with device: {}.", e), EXIT_CODE_AUTH_FAILED).await;
        }
    }

    Ok(())
}
