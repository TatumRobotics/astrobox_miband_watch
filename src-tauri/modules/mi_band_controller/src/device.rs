use anyhow::{Context, Result};
use corelib::device::create_miwear_device;
use corelib::device::xiaomi::r#type::ConnectType;
use tokio::sync::mpsc;

use pb::xiaomi::protocol::{
    wear_packet::{Payload as WearPayload, Type as WearType},
    system::Payload as SystemPayload,
    vibrator_effect::Segment,
    System, WearPacket, VibratorEffect,
    system::SystemId
};

pub struct XiaomiBand {
    pub device_name: String,
    pub device_addr: String,
    pub auth_key: String,
    pub sar_version: u32,
    pub connect_type: ConnectType,
    pub force_android: bool,
}

// this functions triggers a connection with the watch:
// the connect_impl function is a per-platform implementation and causes a lot of trouble
//   on windows, it sometimes fails after the watch is rebooted
//   on linux, during the first few minutes after the watch is rebooted, sometimes it fails with this error:
//      SPP send error: Transport endpoint is not connected (os error 107)
//   that needs to be investigated further.
pub async fn connect_to_device(device_addr: &str) -> Result<()> {
    log::info!("Attempting to connect to {}...", device_addr);
    
    match btclassic_spp::core::connect_impl(device_addr) {
        Ok(true) => {
            log::info!("Connected to device {}", device_addr);
            Ok(())
        }
        Ok(false) => anyhow::bail!("Connection to {} returned false", device_addr),
        Err(e) => anyhow::bail!("Failed to connect to {}: {}", device_addr, e),
    }
}

// the sender's purpose is to tell AstroBox's library what to do with the bluetooth packets. it connects the corelib to the btclassis_spp library.
pub fn create_sender() -> impl Fn(Vec<u8>) -> std::pin::Pin<Box<dyn std::future::Future<Output = Result<(), corelib::device::xiaomi::SendError>> + Send>> + Clone {
    move |data: Vec<u8>| {
        Box::pin(async move {
            btclassic_spp::core::send_impl(&data)
                .map_err(|e| {
                    log::error!("SPP send error: {}", e);
                    corelib::device::xiaomi::SendError::Io(e.to_string())
                })?;
            Ok(())
        })
    }
}

pub async fn create_device(band: XiaomiBand) -> Result<corelib::device::DeviceConnectionInfo> {
    let sender = create_sender();
    
    // this is an AstroBox function that handles authentication with the auth key
    create_miwear_device(
        tokio::runtime::Handle::current(),
        band.device_name,
        band.device_addr,
        band.auth_key,
        band.sar_version,
        band.connect_type,
        band.force_android,
        sender,
    )
    .await
}

pub fn set_data_listener_and_start_subscription(device_addr: String, disconnect_tx: mpsc::UnboundedSender<()>) -> Result<()> {
    let runtime_handle = tokio::runtime::Handle::current();
    
    // listen for disconnects
    btclassic_spp::core::set_data_listener_impl(Box::new(move |result| {
        match result {
            Ok(data) => {
                let rt = runtime_handle.clone();
                let addr = device_addr.clone();
                corelib::device::xiaomi::packet::dispatcher::on_packet(rt, addr, data);
            }
            Err(e) => {
                log::error!("Connection error: {}", e);
                // this will trigger the program to exit, then restart after a delay
                let _ = disconnect_tx.send(());
            }
        }
    }))
    .context("Failed to set data listener")?;
    
    btclassic_spp::core::start_subscription_impl()
        .context("Failed to start subscription")?;
    
    Ok(())
}

// send vibration packets
pub async fn vibrate_pattern(device_addr: &str, segments: Vec<Segment>, name: &str) {
    log::info!("Playing vibration pattern: {}", name);
    
    let device_addr = device_addr.to_string();
    let name = name.to_string();
    
    corelib::ecs::with_rt_mut(move |rt| {
        if let Some(dev) = rt.find_entity_by_id_mut::<corelib::device::xiaomi::XiaomiDevice>(&device_addr) {
            // create packet in the format AstroBox's library expects
            let packet = WearPacket {
                r#type: WearType::System as i32,
                id: SystemId::TestVibrator as u32,
                payload: Some(WearPayload::System(System {
                    payload: Some(SystemPayload::VibratorEffect(VibratorEffect {
                        segments,
                        item: None,
                    })),
                })),
            };
            
            corelib::device::xiaomi::packet::cipher::enqueue_pb_packet(dev, packet, &name);
            log::info!("Vibration packet sent: {}", name);
        } else {
            log::error!("Could not find device in ECS");
        }
    }).await;
}
