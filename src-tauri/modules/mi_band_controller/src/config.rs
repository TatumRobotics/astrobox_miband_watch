use anyhow::{Context, Result};
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use pb::xiaomi::protocol::vibrator_effect::Segment;

pub type PatternConfig = HashMap<String, Vec<Segment>>;

#[derive(Debug, Deserialize, Serialize)]
pub struct DeviceConfig {
    pub name: String,
    pub mac_address: String,
    pub auth_key: String,
    pub sar_version: u32,
    pub connection_delay_ms: Option<u64>,
}

#[derive(Debug, Deserialize, Serialize)]
pub struct WebServerConfig {
    pub port: u16,
    pub vibration_route_name: String,
}

#[derive(Debug, Deserialize, Serialize)]
pub struct Config {
    pub retry_interval_seconds: u64,
    pub patterns: PatternConfig,
    pub device: DeviceConfig,
    pub web_server: WebServerConfig,
}

impl Config {
    pub fn load(path: &str) -> Result<Self> {
        let content = std::fs::read_to_string(path)
            .context(format!("Failed to read config file: {}", path))?;
        let config: Config = serde_yaml::from_str(&content)
            .context("Failed to parse config.yml")?;
        Ok(config)
    }
}
