use anyhow::{Context, Result};
use axum::{
    extract::State,
    http::StatusCode,
    routing::post,
    Json, Router,
};
use serde::{Deserialize, Serialize};
use std::sync::Arc;
use pb::xiaomi::protocol::vibrator_effect::Segment;

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

async fn vibrate_handler(
    State(state): State<AppState>,
    Json(payload): Json<VibrationRequest>,
) -> Result<StatusCode, (StatusCode, Json<ErrorResponse>)> {
    let pattern_name = payload.pattern.to_lowercase();
    
    let segments = match state.patterns.get(&pattern_name).cloned() {
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

    let pattern: Vec<Segment> = segments.into_iter().map(Into::into).collect();
    device::vibrate_pattern(&state.device_addr, pattern, &payload.pattern).await;
    Ok(StatusCode::OK)
}

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
