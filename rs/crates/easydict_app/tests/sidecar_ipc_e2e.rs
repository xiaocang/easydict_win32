use serde_json::{json, Value};
use std::collections::HashMap;
use std::env;
use std::io::{BufRead, BufReader, Write};
use std::path::PathBuf;
use std::process::{Child, ChildStdin, Command, Stdio};
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{mpsc, Arc, Mutex};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

#[test]
fn sidecar_mock_round_trips_health_translate_errors_shutdown_and_stderr() {
    let client = SidecarTestClient::spawn_mock();

    let health = client
        .send_request("health", None, Duration::from_secs(3))
        .expect("health should round-trip");
    assert!(health.get("result").is_some(), "health result: {health}");
    assert!(
        health
            .pointer("/result/capabilities")
            .and_then(Value::as_array)
            .is_some_and(|capabilities| !capabilities.is_empty()),
        "health should report capabilities: {health}"
    );

    let translation = client
        .send_request(
            "translate",
            Some(json!({ "text": "hello", "toLang": "zh" })),
            Duration::from_secs(3),
        )
        .expect("translate should round-trip");
    assert_eq!(
        translation
            .pointer("/result/translatedText")
            .and_then(Value::as_str),
        Some("[zh] hello"),
        "translation response: {translation}"
    );

    let unknown = client
        .send_request("unknown_method_xyz", None, Duration::from_secs(3))
        .expect("unknown method should return a remote error response");
    assert_eq!(
        unknown.pointer("/error/code").and_then(Value::as_str),
        Some("method_not_found"),
        "unknown-method response: {unknown}"
    );

    wait_until(Duration::from_secs(2), || {
        client
            .stderr_logs()
            .iter()
            .any(|line| line.contains("\"level\""))
    });
    assert!(
        client
            .stderr_logs()
            .iter()
            .any(|line| line.contains("\"level\"")),
        "stderr should collect structured JSON logs, got {:?}",
        client.stderr_logs()
    );

    let shutdown = client
        .send_request("shutdown", None, Duration::from_secs(3))
        .expect("shutdown should round-trip");
    assert_eq!(
        shutdown.pointer("/result/ok").and_then(Value::as_bool),
        Some(true),
        "shutdown response: {shutdown}"
    );

    assert_eq!(
        client.wait_for_exit(Duration::from_secs(3)),
        Some(0),
        "shutdown should let the sidecar exit cleanly"
    );
    assert!(
        !client.is_running(),
        "client should observe stopped process"
    );
}

#[test]
fn sidecar_mock_multiplexes_ten_concurrent_requests_by_id() {
    let client = Arc::new(SidecarTestClient::spawn_mock());

    let handles = (0..10)
        .map(|index| {
            let client = Arc::clone(&client);
            thread::spawn(move || {
                let text = format!("message-{index}");
                let response = client
                    .send_request(
                        "translate",
                        Some(json!({ "text": text, "toLang": "en" })),
                        Duration::from_secs(5),
                    )
                    .expect("concurrent translate should round-trip");
                let translated = response
                    .pointer("/result/translatedText")
                    .and_then(Value::as_str)
                    .unwrap_or_default()
                    .to_string();
                (index, translated)
            })
        })
        .collect::<Vec<_>>();

    let results = handles
        .into_iter()
        .map(|handle| handle.join().expect("translate thread should not panic"))
        .collect::<Vec<_>>();

    assert_eq!(results.len(), 10);
    for (index, translated) in results {
        assert!(
            translated.contains(&format!("message-{index}")),
            "response {index} should contain its original text, got {translated:?}"
        );
    }
}

#[test]
fn sidecar_mock_surfaces_request_timeout_and_process_crash() {
    let timeout_client = SidecarTestClient::spawn_mock();
    let timeout = timeout_client
        .send_request(
            "translate",
            Some(json!({ "text": "slow", "toLang": "en", "delayMs": 2_000 })),
            Duration::from_millis(200),
        )
        .expect_err("slow request should time out");
    assert!(
        matches!(timeout, SidecarTestError::Timeout { .. }),
        "expected timeout error, got {timeout:?}"
    );
    drop(timeout_client);

    let crash_client = SidecarTestClient::spawn_mock();
    crash_client
        .send_request("health", None, Duration::from_secs(3))
        .expect("health should succeed before crash");

    let crash = crash_client
        .send_request("crash", None, Duration::from_secs(3))
        .expect_err("crash should end the process before a response");
    assert!(
        matches!(
            crash,
            SidecarTestError::ProcessExited | SidecarTestError::Timeout { .. }
        ),
        "expected process exit or timeout after crash, got {crash:?}"
    );
    assert_eq!(
        crash_client.wait_for_exit(Duration::from_secs(3)),
        Some(2),
        "crash command should exit with the mock's nonzero code"
    );
    assert!(
        !crash_client.is_running(),
        "client should observe stopped process after crash"
    );
}

#[derive(Debug)]
enum SidecarTestError {
    Io(String),
    Json(String),
    Timeout { request_id: String },
    ProcessExited,
}

impl std::fmt::Display for SidecarTestError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Io(message) => write!(f, "I/O error: {message}"),
            Self::Json(message) => write!(f, "JSON error: {message}"),
            Self::Timeout { request_id } => write!(f, "request {request_id} timed out"),
            Self::ProcessExited => write!(f, "sidecar process exited before response"),
        }
    }
}

impl std::error::Error for SidecarTestError {}

struct SidecarTestClient {
    child: Mutex<Child>,
    stdin: Mutex<ChildStdin>,
    shared: Arc<SharedState>,
    next_request: AtomicUsize,
    stdout_thread: Mutex<Option<JoinHandle<()>>>,
    stderr_thread: Mutex<Option<JoinHandle<()>>>,
}

struct SharedState {
    pending: Mutex<HashMap<String, mpsc::Sender<Result<Value, SidecarTestError>>>>,
    stderr_logs: Mutex<Vec<String>>,
    stdout_errors: Mutex<Vec<String>>,
}

impl SidecarTestClient {
    fn spawn_mock() -> Self {
        let mock_service = repo_root().join("sidecar_mock").join("ipc_mock_service.py");
        assert!(
            mock_service.exists(),
            "mock sidecar service should exist at {}",
            mock_service.display()
        );

        let mut child = Command::new(python_executable())
            .arg(mock_service)
            .env("PYTHONUNBUFFERED", "1")
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .spawn()
            .expect("spawn Python mock sidecar service");

        let stdin = child.stdin.take().expect("mock child stdin must be piped");
        let stdout = child
            .stdout
            .take()
            .expect("mock child stdout must be piped");
        let stderr = child
            .stderr
            .take()
            .expect("mock child stderr must be piped");

        let shared = Arc::new(SharedState {
            pending: Mutex::new(HashMap::new()),
            stderr_logs: Mutex::new(Vec::new()),
            stdout_errors: Mutex::new(Vec::new()),
        });

        let stdout_thread = {
            let shared = Arc::clone(&shared);
            thread::spawn(move || read_stdout(stdout, shared))
        };
        let stderr_thread = {
            let shared = Arc::clone(&shared);
            thread::spawn(move || read_stderr(stderr, shared))
        };

        Self {
            child: Mutex::new(child),
            stdin: Mutex::new(stdin),
            shared,
            next_request: AtomicUsize::new(1),
            stdout_thread: Mutex::new(Some(stdout_thread)),
            stderr_thread: Mutex::new(Some(stderr_thread)),
        }
    }

    fn send_request(
        &self,
        method: &str,
        params: Option<Value>,
        timeout: Duration,
    ) -> Result<Value, SidecarTestError> {
        let id = format!(
            "rust-sidecar-{}",
            self.next_request.fetch_add(1, Ordering::Relaxed)
        );
        let (sender, receiver) = mpsc::channel();
        self.shared
            .pending
            .lock()
            .expect("pending lock")
            .insert(id.clone(), sender);

        let payload = json!({
            "id": id,
            "method": method,
            "params": params.unwrap_or_else(|| json!({})),
        });
        let mut line = serde_json::to_string(&payload)
            .map_err(|error| SidecarTestError::Json(error.to_string()))?;
        line.push('\n');

        let write_result = {
            let mut stdin = self.stdin.lock().expect("stdin lock");
            stdin
                .write_all(line.as_bytes())
                .and_then(|_| stdin.flush())
                .map_err(|error| SidecarTestError::Io(error.to_string()))
        };
        if let Err(error) = write_result {
            self.shared
                .pending
                .lock()
                .expect("pending lock")
                .remove(&id);
            return Err(error);
        }

        match receiver.recv_timeout(timeout) {
            Ok(result) => result,
            Err(mpsc::RecvTimeoutError::Timeout) => {
                self.shared
                    .pending
                    .lock()
                    .expect("pending lock")
                    .remove(&id);
                Err(SidecarTestError::Timeout { request_id: id })
            }
            Err(mpsc::RecvTimeoutError::Disconnected) => Err(SidecarTestError::ProcessExited),
        }
    }

    fn stderr_logs(&self) -> Vec<String> {
        self.shared
            .stderr_logs
            .lock()
            .expect("stderr log lock")
            .clone()
    }

    fn wait_for_exit(&self, timeout: Duration) -> Option<i32> {
        let deadline = Instant::now() + timeout;
        loop {
            if let Ok(Some(status)) = self.child.lock().expect("child lock").try_wait() {
                return status.code();
            }

            if Instant::now() >= deadline {
                return None;
            }

            thread::sleep(Duration::from_millis(20));
        }
    }

    fn is_running(&self) -> bool {
        self.child
            .lock()
            .expect("child lock")
            .try_wait()
            .is_ok_and(|status| status.is_none())
    }
}

impl Drop for SidecarTestClient {
    fn drop(&mut self) {
        if let Ok(mut child) = self.child.lock() {
            if child.try_wait().is_ok_and(|status| status.is_none()) {
                let _ = child.kill();
                let _ = child.wait();
            }
        }

        if let Ok(mut handle) = self.stdout_thread.lock() {
            if let Some(handle) = handle.take() {
                let _ = handle.join();
            }
        }
        if let Ok(mut handle) = self.stderr_thread.lock() {
            if let Some(handle) = handle.take() {
                let _ = handle.join();
            }
        }
    }
}

fn read_stdout(stdout: std::process::ChildStdout, shared: Arc<SharedState>) {
    for line in BufReader::new(stdout).lines() {
        let line = match line {
            Ok(line) => line,
            Err(error) => {
                shared
                    .stdout_errors
                    .lock()
                    .expect("stdout error lock")
                    .push(error.to_string());
                break;
            }
        };

        let message = match serde_json::from_str::<Value>(&line) {
            Ok(message) => message,
            Err(error) => {
                shared
                    .stdout_errors
                    .lock()
                    .expect("stdout error lock")
                    .push(format!("{error}: {line}"));
                continue;
            }
        };

        let Some(id) = message
            .get("id")
            .and_then(Value::as_str)
            .map(str::to_string)
        else {
            continue;
        };
        let sender = shared.pending.lock().expect("pending lock").remove(&id);
        if let Some(sender) = sender {
            let _ = sender.send(Ok(message));
        }
    }

    let pending = shared
        .pending
        .lock()
        .expect("pending lock")
        .drain()
        .map(|(_, sender)| sender)
        .collect::<Vec<_>>();
    for sender in pending {
        let _ = sender.send(Err(SidecarTestError::ProcessExited));
    }
}

fn read_stderr(stderr: std::process::ChildStderr, shared: Arc<SharedState>) {
    for line in BufReader::new(stderr).lines() {
        match line {
            Ok(line) => shared
                .stderr_logs
                .lock()
                .expect("stderr log lock")
                .push(line),
            Err(error) => {
                shared
                    .stderr_logs
                    .lock()
                    .expect("stderr log lock")
                    .push(format!("[stderr read error] {error}"));
                break;
            }
        }
    }
}

fn wait_until(timeout: Duration, predicate: impl Fn() -> bool) {
    let deadline = Instant::now() + timeout;
    while Instant::now() < deadline {
        if predicate() {
            return;
        }
        thread::sleep(Duration::from_millis(20));
    }
}

fn repo_root() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .expect("crate parent")
        .parent()
        .expect("rs parent")
        .parent()
        .expect("repo parent")
        .to_path_buf()
}

fn python_executable() -> String {
    if let Ok(value) = env::var("EASYDICT_PYTHON") {
        if !value.trim().is_empty() {
            return value;
        }
    }

    let candidates: &[&str] = if cfg!(windows) {
        &["python", "python3"]
    } else {
        &["python3", "python"]
    };
    for candidate in candidates {
        if Command::new(candidate)
            .arg("--version")
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .status()
            .is_ok_and(|status| status.success())
        {
            return (*candidate).to_string();
        }
    }

    panic!("Python is required to run sidecar_mock/ipc_mock_service.py");
}
