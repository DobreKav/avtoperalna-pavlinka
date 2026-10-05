<?php
// JSON API for the reader agent (agent/PeralnaAgent.exe).
//   POST action=start  machine=W1 uid=04A1B2C3
//   POST action=tick   session_id=12
//   POST action=stop   session_id=12
//   GET  action=ping
//   POST action=status  (every 2 s; the reply carries the PLC link settings from plc.php)
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
            // What the agent is actually doing with the PLC link, for plc.php.
            setting('plc_link', mb_substr((string)($input['plc_link'] ?? ''), 0, 120));
            setting('plc_error', mb_substr((string)($input['plc_error'] ?? ''), 0, 200));
            setting('local_ips', mb_substr(preg_replace('/[^0-9.,]/', '', (string)($input['local_ips'] ?? '')) ?? '', 0, 200));
            setting('agent_version', mb_substr((string)($input['version'] ?? ''), 0, 20));
            // First contact after an upgrade: adopt what the agent already runs (from its
            // agent.ini) instead of overriding it with the defaults.
            if (setting('plc_mode') === null && isset($input['cur_mode'])) {
                save_plc_settings([
                    'plc_mode' => $input['cur_mode'],
                    'plc_listen_ip' => $input['cur_listen_ip'] ?? '',
                    'plc_listen_port' => $input['cur_listen_port'] ?? '',
                    'plc_allowed_ip' => $input['cur_allowed_ip'] ?? '',
                    'plc_ip' => $input['cur_plc_ip'] ?? '',
                    'plc_port' => $input['cur_plc_port'] ?? '',
                    'plc_unit_id' => PLC_DEFAULTS['plc_unit_id'],
                    'plc_poll_ms' => PLC_DEFAULTS['plc_poll_ms'],
                    'test_mode' => setting('test_mode') === '1',
                ]);
            }
            // The reply carries the PLC settings: the agent applies changes within seconds.
            reply(['ok' => true, 'config' => plc_settings()]);
        case 'start':
            reply(begin_machine_session((string)($input['machine'] ?? ''), (string)($input['uid'] ?? '')));
        case 'tick':
            $active = isset($input['active']) ? $input['active'] === '1' : null;
            reply(tick_machine_session((int)($input['session_id'] ?? 0), $active));
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
