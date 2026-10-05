<?php
// Billing regression. Runs against a throwaway database:
//   php tests\billing-test.php
declare(strict_types=1);

$dbFile = sys_get_temp_dir() . '/peralna-test-' . getmypid() . '.sqlite';
putenv('PERALNA_DB=' . $dbFile);
$root = dirname(__DIR__);
require $root . '/app/lib.php';
db()->exec("INSERT INTO machines (code, name, type, price_per_minute, min_balance) VALUES
    ('W1', 'Test W1', 'washer', 5, 50), ('W2', 'Test W2', 'washer', 5, 50), ('D1', 'Test D1', 'dryer', 4, 40)");

$failures = 0;
function check(string $label, $actual, $expected): void
{
    global $failures;
    if ($actual === $expected) {
        echo "ok   $label\n";
    } else {
        $failures++;
        echo "FAIL $label: expected " . var_export($expected, true) . ', got ' . var_export($actual, true) . "\n";
    }
}

// Pretend the session began $seconds ago (and was last seen $seenAgo ago).
function age_session(int $id, int $seconds, ?int $seenAgo = null): void
{
    // Spraying (foam or water) the whole time since the start.
    db()->prepare('UPDATE sessions SET started_at = ?, active_since = ?, active_seconds = 0, last_seen_at = ? WHERE id = ?')->execute([
        gmdate('Y-m-d H:i:s', time() - $seconds),
        gmdate('Y-m-d H:i:s', time() - $seconds),
        gmdate('Y-m-d H:i:s', time() - ($seenAgo ?? $seconds)),
        $id,
    ]);
}

function balance(int $cardId): int
{
    $stmt = db()->prepare('SELECT balance FROM cards WHERE id = ?');
    $stmt->execute([$cardId]);
    return (int)$stmt->fetchColumn();
}

function reason(callable $fn): string
{
    try { $fn(); return 'no error'; } catch (WalletError $e) { return $e->reason; }
}

db()->exec("INSERT INTO cards (uid, holder_name) VALUES ('04A1B2C3', 'Test A'), ('04FFEE11', 'Test B')");
$a = (int)find_card_by_uid('04:a1:b2:c3')['id'];
$b = (int)find_card_by_uid('04FFEE11')['id'];

check('unknown card is refused and remembered', reason(fn() => begin_machine_session('W1', 'DEADBEEF')), 'unknown_card');
check('unknown uid stored for registration', setting('last_unknown_uid'), 'DEADBEEF');
check('empty card cannot start', reason(fn() => begin_machine_session('W1', '04A1B2C3')), 'no_balance');

apply_transaction($a, 'topup', 100, ['note' => 'test']);
check('below min balance (40 < 50) is refused', reason(fn() => apply_transaction($a, 'adjust', -60) && begin_machine_session('W1', '04A1B2C3')), 'no_balance');
apply_transaction($a, 'topup', 60);
check('balance after top-ups', balance($a), 100);

// W1 costs 5 den/min.
$s = begin_machine_session('W1', '04A1B2C3');
check('session starts', $s['running'], true);
check('seconds left for 100 den at 5/min', $s['seconds_left'], 1200);
check('card B with 0 den is refused', reason(fn() => begin_machine_session('W2', '04FFEE11')), 'no_balance');

age_session($s['session_id'], 90);
$t = tick_machine_session($s['session_id']);
check('90 s at 5/min charges 8 den (rounded up)', $t['charged'], 8);
check('balance drops while card is inside', balance($a), 92);

$restart = begin_machine_session('W1', '04A1B2C3');
check('reader restart resumes same session', $restart['session_id'], $s['session_id']);

age_session($s['session_id'], 180);
$done = finish_machine_session($s['session_id'], 'removed', 'api');
check('3 min charges 15 den in total', $done['charged'], 15);
check('balance after removal', balance($a), 85);
$tx = db()->query("SELECT amount, balance_after, note FROM transactions WHERE type = 'charge'")->fetchAll();
check('one ledger row per session', count($tx), 1);
check('ledger amount', (int)$tx[0]['amount'], -15);
check('ledger balance_after', (int)$tx[0]['balance_after'], 85);
check('second finish is harmless', finish_machine_session($s['session_id'], 'removed', 'api')['charged'], 15);

// Running out of money stops the machine.
$s2 = begin_machine_session('W2', '04A1B2C3');
age_session($s2['session_id'], 3600);
$t2 = tick_machine_session($s2['session_id']);
check('exhausted balance stops the session', $t2['running'], false);
check('never below zero', balance($a), 0);
check('stop reason', $t2['end_reason'], 'no_balance');
check('charged exactly what was left', $t2['charged'], 85);

// Silent reader: billed only up to its last report.
apply_transaction($b, 'topup', 500);
$s3 = begin_machine_session('D1', '04FFEE11');
age_session($s3['session_id'], 600, 300); // started 10 min ago, last report 5 min ago
close_stale_sessions();
$row = db()->query('SELECT status, end_reason, charged FROM sessions WHERE id = ' . (int)$s3['session_id'])->fetch();
check('stale session is closed', $row['status'], 'ended');
check('stale reason', $row['end_reason'], 'timeout');
check('billed only to last report (5 min at 4/min)', (int)$row['charged'], 20);
check('card B balance', balance($b), 480);

// A new card in a reader closes the previous card's session there.
$s4 = begin_machine_session('D1', '04FFEE11');
apply_transaction($a, 'topup', 200);
$s5 = begin_machine_session('D1', '04A1B2C3');
$old = db()->query('SELECT status, end_reason FROM sessions WHERE id = ' . (int)$s4['session_id'])->fetch();
check('previous card session ended', $old['end_reason'], 'removed');
check('new card runs', $s5['running'], true);

// СТОП: nothing is charged while paused; foam/water resumes billing.
$p = begin_machine_session('W2', '04A1B2C3');
check('new session starts paused', $p['active'], false);
db()->prepare('UPDATE sessions SET started_at = ? WHERE id = ?')->execute([gmdate('Y-m-d H:i:s', time() - 600), $p['session_id']]);
$t = tick_machine_session($p['session_id']);
check('10 min in the reader without foam/water costs nothing', $t['charged'], 0);
tick_machine_session($p['session_id'], true);
db()->prepare('UPDATE sessions SET active_since = ? WHERE id = ?')->execute([gmdate('Y-m-d H:i:s', time() - 120), $p['session_id']]);
$t = tick_machine_session($p['session_id'], false); // СТОП after 2 min of water
check('2 min spraying at 5/min = 10 den, then paused', [$t['charged'], $t['active']], [10, false]);
db()->prepare('UPDATE sessions SET last_seen_at = ? WHERE id = ?')->execute([gmdate('Y-m-d H:i:s', time()), $p['session_id']]);
$t = tick_machine_session($p['session_id']);
check('still 10 den while paused', $t['charged'], 10);
$done = finish_machine_session($p['session_id'], 'removed', 'api');
check('card out after the pause: 10 den in total', $done['charged'], 10);

// Demo card: exists after setup and refills itself when low.
$demo = find_card_by_uid(DEMO_UID);
check('demo card exists', $demo !== null && $demo['holder_name'] === 'Демо картичка', true);
check('demo card refused outside test mode', reason(function () {
    begin_machine_session('W2', DEMO_UID);
}), 'blocked');
setting('test_mode', '1');
db()->prepare('UPDATE cards SET balance = 30 WHERE uid = ?')->execute([DEMO_UID]);
$d = begin_machine_session('W2', DEMO_UID);
check('demo card refills to 1000 and starts', [$d['running'], $d['balance']], [true, DEMO_BALANCE]);
finish_machine_session($d['session_id'], 'removed', 'api');

check('blocked card cannot start', reason(function () use ($b) {
    db()->prepare("UPDATE cards SET status = 'blocked' WHERE id = ?")->execute([$b]);
    begin_machine_session('W1', '04FFEE11');
}), 'blocked');

// PLC link settings (plc.php).
check('plc defaults: laptop is the server on 502', [plc_settings()['plc_mode'], plc_settings()['plc_listen_port']], ['server', '502']);
check('client mode saved', save_plc_settings(['plc_mode' => 'client', 'plc_ip' => '192.168.0.50', 'plc_port' => '1502', 'plc_unit_id' => '1', 'plc_poll_ms' => '200']), []);
check('client mode settings read back', [plc_settings()['plc_mode'], plc_settings()['plc_ip'], plc_settings()['plc_port']], ['client', '192.168.0.50', '1502']);
check('server values kept while in client mode', plc_settings()['plc_listen_port'], '502');
check('bad PLC IP refused', count(save_plc_settings(['plc_mode' => 'client', 'plc_ip' => '192.168.0.300', 'plc_port' => '502', 'plc_unit_id' => '1', 'plc_poll_ms' => '200'])), 1);
check('0.0.0.0 is not a PLC address', count(save_plc_settings(['plc_mode' => 'client', 'plc_ip' => '0.0.0.0', 'plc_port' => '502', 'plc_unit_id' => '1', 'plc_poll_ms' => '200'])), 1);
check('bad port refused', count(save_plc_settings(['plc_mode' => 'server', 'plc_listen_ip' => '0.0.0.0', 'plc_listen_port' => '70000'])), 1);
check('refused input changes nothing', plc_settings()['plc_ip'], '192.168.0.50');
check('server mode with an allowed PLC', save_plc_settings(['plc_mode' => 'server', 'plc_listen_ip' => '', 'plc_listen_port' => '5020', 'plc_allowed_ip' => '192.168.1.20']), []);
check('empty listen IP means all', [plc_settings()['plc_listen_ip'], plc_settings()['plc_allowed_ip'], plc_settings()['test_mode']], ['0.0.0.0', '192.168.1.20', '0']);
check('test mode off again after save without it', test_mode(), false);

foreach (glob($dbFile . '*') as $f) @unlink($f);
echo $failures ? "\n$failures FAILED\n" : "\nALL PASSED\n";
exit($failures ? 1 : 0);
