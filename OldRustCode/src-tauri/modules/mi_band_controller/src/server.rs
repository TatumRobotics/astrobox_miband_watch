use anyhow::{Context, Result};
use axum::{
    extract::State,
    http::StatusCode,
    routing::post,
    Json, Router,
};
use serde::{Deserialize, Serialize};
use std::sync::Arc;
use crate::config::PatternConfig;
use crate::device;

#[derive(Deserialize)]
struct VibrationRequest {
    pattern: String,
}

#[derive(Serialize)]
struct ErrorResponse {
    error: String,
    available_patterns: Vec<String>,
}

#[derive(Clone)]
pub struct AppState {
    pub device_addr: String,
    pub patterns: Arc<PatternConfig>,
}

// parses the pattern and picks the correct one from the config (if it exists!). then triggers the bluetooth command.
async fn vibrate_handler(
    State(state): State<AppState>,
    Json(payload): Json<VibrationRequest>,
) -> Result<StatusCode, (StatusCode, Json<ErrorResponse>)> {
    let pattern_name = payload.pattern.to_lowercase();
    
    // patterns are made up of many segments.
    // segments are made up of on/off, duration, and strength.
    // that's defined by the xiaomi protocol.
    let pattern = match state.patterns.get(&pattern_name).cloned() {
        Some(seg) => seg,
        None => {
            let available_patterns: Vec<String> = state.patterns.keys().cloned().collect();
            return Err((
                StatusCode::BAD_REQUEST,
                Json(ErrorResponse {
                    error: format!("Pattern '{}' not found", pattern_name),
                    available_patterns,
                }),
            ));
        }
    };

    device::vibrate_pattern(&state.device_addr, pattern, &payload.pattern).await;
    Ok(StatusCode::OK)
}

// listen for vibration requests at the configured port
pub async fn start_server(
    app_state: AppState,
    route: String,
    port: u16,
) -> Result<(tokio::net::TcpListener, Router)> {
    let app = Router::new()
        .route(&route, post(vibrate_handler))
        .with_state(app_state);

    let bind_addr = format!("0.0.0.0:{}", port);
    let listener = tokio::net::TcpListener::bind(&bind_addr)
        .await
        .context(format!("Failed to host the server at {}", bind_addr))?;

    log::info!("Web server listening on http://0.0.0.0:{} for vibration requests!!", port);
    Ok((listener, app))
}
