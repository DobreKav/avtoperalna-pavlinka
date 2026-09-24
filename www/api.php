<?php
// JSON API for the reader agent (agent/PeralnaAgent.exe).
//   POST action=start  machine=W1 uid=04A1B2C3
//   POST action=tick   session_id=12
//   POST action=stop   session_id=12
//   GET  action=ping
// With an empty api_key in config.php only this computer may call it.
declare(strict_types=1);
require dirname(__DIR__) . '/app/lib.php';

header('Content-Type: application/json; charset=utf-8');
header('Cache-Control: no-store');

function reply(array $data, int $status = 200): void
{
    http_response_code($status);
    echo json_encode($data, JSON_UNESCAPED_UNICODE);
    exit;
}

$key = (string)config('api_key');
if ($key === '') {
    if (!in_array($_SERVER['REMOTE_ADDR'] ?? '', ['127.0.0.1', '::1'], true)) {
        reply(['ok' => false, 'reason' => 'forbidden', 'message' => 'API is local-only until api_key is set.'], 403);
    }
} elseif (!hash_equals($key, (string)($_SERVER['HTTP_X_API_KEY'] ?? ''))) {
    reply(['ok' => false, 'reason' => 'forbidden', 'message' => 'Bad API key.'], 403);
}

$input = $_POST;
if (!$input && str_contains((string)($_SERVER['CONTENT_TYPE'] ?? ''), 'json')) {
    $input = json_decode((string)file_get_contents('php://input'), true) ?: [];
}
$action = (string)($input['action'] ?? $_GET['action'] ?? '');

try {
    setting('agent_seen_at', utc_now());
    switch ($action) {
        case 'ping':
            close_stale_sessions();
            reply(['ok' => true, 'time' => utc_now()]);
        case 'status':
            // Reader and PLC link, reported by the agent every 5 s.
            setting('status_at', utc_now());
            setting('reader_name', mb_substr((string)($input['reader'] ?? ''), 0, 200));
            setting('card_present', ($input['card'] ?? '') === '1' ? '1' : '0');
            setting('plc_ok', ($input['plc'] ?? '') === '1' ? '1' : '0');
            setting('plc_peer', mb_substr(preg_replace('/[^0-9a-fA-F.:]/', '', (string)($input['plc_peer'] ?? '')) ?? '', 0, 45));
            reply(['ok' => true]);
        case 'start':
            reply(begin_machine_session((string)($input['machine'] ?? ''), (string)($input['uid'] ?? '')));
        case 'tick':
            reply(tick_machine_session((int)($input['session_id'] ?? 0)));
        case 'stop':
            reply(finish_machine_session((int)($input['session_id'] ?? 0), 'removed', 'api'));
        default:
            reply(['ok' => false, 'reason' => 'bad_action', 'message' => 'Unknown action.'], 400);
    }
} catch (WalletError $err) {
    reply(['ok' => false, 'running' => false, 'reason' => $err->reason, 'message' => $err->getMessage()]);
} catch (Throwable $err) {
    error_log('peralna api: ' . $err);
    reply(['ok' => false, 'running' => false, 'reason' => 'server_error', 'message' => 'Server error.'], 500);
}
