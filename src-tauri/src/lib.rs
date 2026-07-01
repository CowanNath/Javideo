// Javideo Tauri shell. Responsibilities:
//   1. Spawn the .NET worker (`javideo-worker`) as a sidecar.
//   2. Read the `JAVIDEO_WORKER_PORT=<port>` line from its stdout.
//   3. Expose the resolved worker URL to the frontend via the `get_worker_url` command.
//   4. Kill the worker when the app exits.

use std::sync::Mutex;
use tauri::Manager;
use tauri_plugin_shell::ShellExt;
use tauri_plugin_shell::process::{CommandEvent as EventProcess, CommandChild};

/// Holds the worker base URL once the handshake line has been read.
struct WorkerState {
    url: Mutex<Option<String>>,
    child: Mutex<Option<CommandChild>>,
    close_behavior: Mutex<String>,
}

#[tauri::command]
fn get_worker_url(state: tauri::State<WorkerState>) -> Option<String> {
    state.url.lock().ok()?.clone()
}

/// Open DevTools on the main window (called when the user enables debug mode).
#[tauri::command]
fn open_devtools(app: tauri::AppHandle) {
    if let Some(win) = app.get_webview_window("main") {
        win.open_devtools();
    }
}

/// Set the close-behavior preference (stored in app state for on_window_event).
#[tauri::command]
fn set_close_behavior(behavior: String, state: tauri::State<WorkerState>) {
    if let Ok(mut v) = state.close_behavior.lock() {
        *v = behavior;
    }
}

/// Open a folder in the system file explorer.
#[tauri::command]
fn shell_open(path: String) {
    // On Windows, explorer.exe opens the folder directly.
    #[cfg(target_os = "windows")]
    let _ = std::process::Command::new("explorer.exe").arg(&path).spawn();
    #[cfg(not(target_os = "windows"))]
    let _ = open::that(&path);
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_shell::init())
        .plugin(tauri_plugin_dialog::init())
        .manage(WorkerState {
            url: Mutex::new(None),
            child: Mutex::new(None),
            close_behavior: Mutex::new("quit".to_string()),
        })
        .setup(|app| {
            // Spawn the bundled sidecar. Tauri resolves `javideo-worker` to the
            // platform-specific binary (javideo-worker.exe on Windows).
            let shell = app.shell();
            let sidecar = shell
                .sidecar("javideo-worker")
                .expect("failed to find javideo-worker sidecar");

            let (mut rx, child) = sidecar
                .args(["--urls", "http://127.0.0.1:0"])
                .spawn()
                .expect("failed to spawn worker sidecar");

            // Store the child handle so we can kill it on exit.
            let state: tauri::State<WorkerState> = app.state();
            *state.child.lock().unwrap() = Some(child);

            // Read stdout lines until we see the port handshake, then store the URL.
            let app_handle = app.handle().clone();
            tauri::async_runtime::spawn(async move {
                while let Some(event) = rx.recv().await {
                    match event {
                        EventProcess::Stdout(bytes) => {
                            let line = String::from_utf8_lossy(&bytes);
                            for l in line.lines() {
                                if let Some(rest) = l.strip_prefix("JAVIDEO_WORKER_PORT=") {
                                    let port = rest.trim();
                                    let url = format!("http://127.0.0.1:{}", port);
                                    eprintln!("[shell] worker ready at {}", url);
                                    let st: tauri::State<WorkerState> = app_handle.state();
                                    *st.url.lock().unwrap() = Some(url);
                                }
                            }
                        }
                        EventProcess::Stderr(bytes) => {
                            eprint!("[worker] {}", String::from_utf8_lossy(&bytes));
                        }
                        EventProcess::Terminated(payload) => {
                            eprintln!("[shell] worker exited: {:?}", payload);
                        }
                        _ => {}
                    }
                }
            });

            // System tray so "minimize to tray" has a way back.
            let tray = tauri::tray::TrayIconBuilder::with_id("main-tray")
                .tooltip("Javideo")
                .icon(app.default_window_icon().cloned().unwrap())
                .menu(&tauri::menu::Menu::with_items(app, &[
                    &tauri::menu::MenuItem::with_id(app, "show", "显示窗口", true, None::<&str>)?,
                    &tauri::menu::MenuItem::with_id(app, "quit", "退出", true, None::<&str>)?,
                ])?)
                .on_menu_event(|app, event| match event.id().as_ref() {
                    "show" => {
                        if let Some(win) = app.get_webview_window("main") {
                            let _ = win.show();
                            let _ = win.set_focus();
                        }
                    }
                    "quit" => {
                        if let Some(win) = app.get_webview_window("main") {
                            let _ = win.destroy();
                        }
                    }
                    _ => {}
                })
                .on_tray_icon_event(|tray, event| {
                    if let tauri::tray::TrayIconEvent::DoubleClick { .. } = event {
                        let app = tray.app_handle();
                        if let Some(win) = app.get_webview_window("main") {
                            let _ = win.show();
                            let _ = win.set_focus();
                        }
                    }
                })
                .build(app)?;
            let _ = tray; // keep alive

            Ok(())
        })
        .on_window_event(|window, event| {
            let state: tauri::State<WorkerState> = window.state();
            // Close behavior: "tray" = minimize to tray (prevent close), "quit" = exit.
            if let tauri::WindowEvent::CloseRequested { api, .. } = event {
                let behavior = state.close_behavior.lock().unwrap().clone();
                if behavior == "tray" {
                    // Prevent the window from closing; hide it instead.
                    api.prevent_close();
                    let _ = window.hide();
                }
            }
            // Kill the worker when the window is actually destroyed (quit).
            if let tauri::WindowEvent::Destroyed = event {
                let child = { state.child.lock().unwrap().take() };
                if let Some(child) = child {
                    let _ = child.kill();
                }
            }
        })
        .invoke_handler(tauri::generate_handler![get_worker_url, open_devtools, set_close_behavior, shell_open])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
