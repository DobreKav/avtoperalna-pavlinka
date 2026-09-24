<?php
declare(strict_types=1);

const APP_NAME = 'Автоперална Павлинка';

// Card for trying the bay with the CardEmulator add-on. It refills itself and is
// left out of the daily totals on the dashboard.
const DEMO_UID = 'DEMO0001';
const DEMO_BALANCE = 1000;

$CONFIG = require __DIR__ . '/config.php';
date_default_timezone_set($CONFIG['timezone']);

function config(string $key)
{
    global $CONFIG;
    return $CONFIG[$key] ?? null;
}

function db(): PDO
{
    static $pdo = null;
    if ($pdo === null) {
        $path = (string)config('db_path');
        if (!is_dir(dirname($path))) mkdir(dirname($path), 0777, true);
        $pdo = new PDO('sqlite:' . $path, null, null, [
            PDO::ATTR_ERRMODE => PDO::ERRMODE_EXCEPTION,
            PDO::ATTR_DEFAULT_FETCH_MODE => PDO::FETCH_ASSOC,
        ]);
        $pdo->exec('PRAGMA foreign_keys = ON');
        $pdo->exec('PRAGMA busy_timeout = 10000');
        $pdo->exec('PRAGMA journal_mode = WAL');
        $pdo->exec('PRAGMA synchronous = FULL');
        if ((int)$pdo->query('PRAGMA user_version')->fetchColumn() < 1) {
            $pdo->exec(file_get_contents(__DIR__ . '/schema.sql'));
            $pdo->exec('PRAGMA user_version = 1');
        }
        if ((int)$pdo->query('PRAGMA user_version')->fetchColumn() < 2) {
            $pdo->prepare("INSERT OR IGNORE INTO cards (uid, holder_name, balance, created_at) VALUES (?, 'Демо картичка', ?, datetime('now'))")
                ->execute([DEMO_UID, DEMO_BALANCE]);
            $pdo->exec('PRAGMA user_version = 2');
        }
        if ((int)$pdo->query('PRAGMA user_version')->fetchColumn() < 3) {
            // Billing runs only while foam or water is on (the PLC reports it). active_since is
            // NULL while paused; active_seconds holds the time of the finished active stretches.
            $pdo->exec('ALTER TABLE sessions ADD COLUMN active_seconds INTEGER NOT NULL DEFAULT 0');
            $pdo->exec('ALTER TABLE sessions ADD COLUMN active_since TEXT NULL');
            $pdo->exec('PRAGMA user_version = 3');
        }
    }
    return $pdo;
}

// Every money movement runs inside BEGIN IMMEDIATE: SQLite then allows one
// writer at a time, which is what the row locks did on MySQL.
function tx_begin(): void
{
    db()->exec('BEGIN IMMEDIATE');
    $GLOBALS['__tx_active'] = true;
}

function tx_commit(): void
{
    db()->exec('COMMIT');
    $GLOBALS['__tx_active'] = false;
}

function tx_rollback(): void
{
    if (!empty($GLOBALS['__tx_active'])) db()->exec('ROLLBACK');
    $GLOBALS['__tx_active'] = false;
}

function e($value): string
{
    return htmlspecialchars((string)$value, ENT_QUOTES, 'UTF-8');
}

function money(int $amount): string
{
    return number_format($amount, 0, ',', '.') . ' ' . config('currency');
}

function utc_now(): string
{
    return gmdate('Y-m-d H:i:s');
}

function utc_ts(string $utc): int
{
    return strtotime($utc . ' UTC');
}

function local_time(?string $utc, string $format = 'd.m.Y H:i'): string
{
    return $utc ? date($format, utc_ts($utc)) : '';
}

function duration(int $seconds): string
{
    $seconds = max(0, $seconds);
    $h = intdiv($seconds, 3600);
    $m = intdiv($seconds % 3600, 60);
    $s = $seconds % 60;
    return $h ? sprintf('%d:%02d:%02d', $h, $m, $s) : sprintf('%d:%02d', $m, $s);
}

// Readers report the UID as hex, sometimes with spaces or colons.
function normalize_uid(string $uid): string
{
    return strtoupper(preg_replace('/[^0-9A-Za-z]/', '', $uid) ?? '');
}

function redirect(string $url): void
{
    header('Location: ' . $url);
    exit;
}

function flash(?string $message = null, string $kind = 'ok'): ?array
{
    if ($message !== null) {
        $_SESSION['flash'] = ['message' => $message, 'kind' => $kind];
        return null;
    }
    $f = $_SESSION['flash'] ?? null;
    unset($_SESSION['flash']);
    return $f;
}

function setting(string $key, ?string $value = null): ?string
{
    if ($value !== null) {
        db()->prepare('REPLACE INTO settings (name, value) VALUES (?, ?)')->execute([$key, $value]);
        return $value;
    }
    $stmt = db()->prepare('SELECT value FROM settings WHERE name = ?');
    $stmt->execute([$key]);
    $v = $stmt->fetchColumn();
    return $v === false ? null : (string)$v;
}

// ─── Session, login, CSRF ───────────────────────────────────────

function start_php_session(): void
{
    if (session_status() === PHP_SESSION_NONE) {
        session_set_cookie_params(['httponly' => true, 'samesite' => 'Strict']);
        session_start();
    }
}

function current_user(): ?array
{
    return $_SESSION['user'] ?? null;
}

function require_login(): array
{
    start_php_session();
    $user = current_user();
    if (!$user) redirect('login.php');
    return $user;
}

function csrf_token(): string
{
    if (empty($_SESSION['csrf'])) $_SESSION['csrf'] = bin2hex(random_bytes(32));
    return $_SESSION['csrf'];
}

function csrf_field(): string
{
    return '<input type="hidden" name="csrf" value="' . e(csrf_token()) . '">';
}

function check_csrf(): void
{
    if (!hash_equals($_SESSION['csrf'] ?? '', (string)($_POST['csrf'] ?? ''))) {
        http_response_code(400);
        exit('Невалиден формулар. Освежи ја страницата и обиди се повторно.');
    }
}

// ─── Ledger ─────────────────────────────────────────────────────

class WalletError extends RuntimeException
{
    public string $reason;

    public function __construct(string $message, string $reason = 'error')
    {
        parent::__construct($message);
        $this->reason = $reason;
    }
}

const TX_LABELS = [
    'topup' => 'Полнење',
    'charge' => 'Перење/сушење',
    'refund' => 'Враќање',
    'adjust' => 'Корекција',
];

const END_REASONS = [
    'removed' => 'Картичката е извадена',
    'no_balance' => 'Нема повеќе средства',
    'timeout' => 'Читачот не се јави',
    'admin' => 'Запрено од админ',
];

/**
 * Top-ups, refunds and manual corrections. $amount is signed.
 * Returns the new balance.
 */
function apply_transaction(int $cardId, string $type, int $amount, array $opts = []): int
{
    if ($amount === 0) throw new WalletError('Износот не може да биде 0.');
    tx_begin();
    try {
        $card = lock_card($cardId);
        $newBalance = (int)$card['balance'] + $amount;
        if ($newBalance < 0) {
            throw new WalletError('Нема доволно средства. Салдо: ' . money((int)$card['balance']), 'no_balance');
        }
        db()->prepare('UPDATE cards SET balance = ? WHERE id = ?')->execute([$newBalance, $cardId]);
        insert_ledger($cardId, $type, $amount, $newBalance, $opts);
        tx_commit();
        return $newBalance;
    } catch (Throwable $err) {
        tx_rollback();
        throw $err;
    }
}

function lock_card(int $cardId): array
{
    $stmt = db()->prepare('SELECT * FROM cards WHERE id = ?');
    $stmt->execute([$cardId]);
    $card = $stmt->fetch();
    if (!$card) throw new WalletError('Картичката не постои.', 'unknown_card');
    return $card;
}

function insert_ledger(int $cardId, string $type, int $amount, int $balanceAfter, array $opts): void
{
    db()->prepare(
        'INSERT INTO transactions (card_id, type, amount, balance_after, session_id, machine_id, note, source, user_id, created_at)
         VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)'
    )->execute([
        $cardId,
        $type,
        $amount,
        $balanceAfter,
        $opts['session_id'] ?? null,
        $opts['machine_id'] ?? null,
        mb_substr((string)($opts['note'] ?? ''), 0, 200),
        $opts['source'] ?? 'admin',
        $opts['user_id'] ?? null,
        utc_now(),
    ]);
}

function find_card_by_uid(string $uid): ?array
{
    $stmt = db()->prepare('SELECT * FROM cards WHERE uid = ?');
    $stmt->execute([normalize_uid($uid)]);
    return $stmt->fetch() ?: null;
}

function find_machine(string $code): ?array
{
    $stmt = db()->prepare('SELECT * FROM machines WHERE code = ?');
    $stmt->execute([strtoupper(trim($code))]);
    return $stmt->fetch() ?: null;
}

// ─── Machine sessions (card sits in the reader) ─────────────────
//
// The card is billed per second at rate_per_minute / 60, rounded up to whole
// denars, but only while the bay is spraying (foam or water chosen on the PLC).
// A session starts paused; СТОП on the bay pauses it again. The balance on the
// card goes down on every reader report; the ledger gets one row per session
// when the card comes out.

function seconds_left(int $balance, int $ratePerMinute): int
{
    return $ratePerMinute > 0 ? intdiv($balance * 60, $ratePerMinute) : 0;
}

function lock_session(int $sessionId): array
{
    $stmt = db()->prepare('SELECT * FROM sessions WHERE id = ?');
    $stmt->execute([$sessionId]);
    $session = $stmt->fetch();
    if (!$session) throw new WalletError('Сесијата не постои.', 'unknown_session');
    return $session;
}

/**
 * Charges everything owed up to $untilUtc. Must run inside a DB transaction
 * with the session and card rows locked. Returns [session, card, exhausted].
 */
function active_seconds(array $session, string $untilUtc): int
{
    $seconds = (int)$session['active_seconds'];
    if ($session['active_since'] !== null) {
        $seconds += max(0, utc_ts($untilUtc) - utc_ts($session['active_since']));
    }
    return $seconds;
}

/** Starts or pauses the billing clock at $atUtc. Runs inside the session's transaction. */
function set_active(array $session, bool $active, string $atUtc): array
{
    if ($active && $session['active_since'] === null) {
        $session['active_since'] = $atUtc;
    } elseif (!$active && $session['active_since'] !== null) {
        $session['active_seconds'] = active_seconds($session, $atUtc);
        $session['active_since'] = null;
    } else {
        return $session;
    }
    db()->prepare('UPDATE sessions SET active_seconds = ?, active_since = ? WHERE id = ?')
        ->execute([$session['active_seconds'], $session['active_since'], $session['id']]);
    return $session;
}

function accrue(array $session, array $card, string $untilUtc): array
{
    $elapsed = active_seconds($session, $untilUtc);
    $due = (int)ceil($elapsed * (int)$session['rate_per_minute'] / 60);
    $delta = max(0, $due - (int)$session['charged']);
    $exhausted = false;
    if ($delta >= (int)$card['balance']) {
        $delta = (int)$card['balance'];
        $exhausted = $due > (int)$session['charged'];
    }
    if ($delta > 0) {
        $card['balance'] = (int)$card['balance'] - $delta;
        $session['charged'] = (int)$session['charged'] + $delta;
        db()->prepare('UPDATE cards SET balance = ? WHERE id = ?')->execute([$card['balance'], $card['id']]);
    }
    $session['last_seen_at'] = max($session['last_seen_at'], $untilUtc);
    db()->prepare('UPDATE sessions SET charged = ?, last_seen_at = ? WHERE id = ?')
        ->execute([$session['charged'], $session['last_seen_at'], $session['id']]);
    return [$session, $card, $exhausted];
}

function end_session(array $session, array $card, string $reason, string $source, ?int $userId = null): array
{
    $endedAt = $reason === 'timeout' ? $session['last_seen_at'] : utc_now();
    $session = set_active($session, false, $endedAt);
    db()->prepare("UPDATE sessions SET status = 'ended', ended_at = ?, end_reason = ? WHERE id = ?")
        ->execute([$endedAt, $reason, $session['id']]);
    if ((int)$session['charged'] > 0) {
        $minutes = (int)ceil((int)$session['active_seconds'] / 60);
        insert_ledger((int)$card['id'], 'charge', -(int)$session['charged'], (int)$card['balance'], [
            'session_id' => (int)$session['id'],
            'machine_id' => (int)$session['machine_id'],
            'note' => $minutes . ' мин.',
            'source' => $source,
            'user_id' => $userId,
        ]);
    }
    $session['status'] = 'ended';
    $session['ended_at'] = $endedAt;
    $session['end_reason'] = $reason;
    return $session;
}

function session_payload(array $session, array $card, bool $running): array
{
    return [
        'ok' => true,
        'running' => $running,
        'session_id' => (int)$session['id'],
        'balance' => (int)$card['balance'],
        'charged' => (int)$session['charged'],
        'rate_per_minute' => (int)$session['rate_per_minute'],
        'seconds_left' => $running ? seconds_left((int)$card['balance'], (int)$session['rate_per_minute']) : 0,
        'end_reason' => $session['end_reason'] ?? null,
        'holder_name' => (string)($card['holder_name'] ?: $card['uid']),
        'active' => $running && ($session['active_since'] ?? null) !== null,
    ];
}

/** Closes sessions whose reader stopped reporting. Billing stops at the last report. */
function close_stale_sessions(): void
{
    $cutoff = gmdate('Y-m-d H:i:s', time() - (int)config('session_timeout_seconds'));
    $stmt = db()->prepare("SELECT id FROM sessions WHERE status = 'running' AND last_seen_at < ?");
    $stmt->execute([$cutoff]);
    foreach ($stmt->fetchAll(PDO::FETCH_COLUMN) as $id) {
        finish_machine_session((int)$id, 'timeout', 'api');
    }
}

/** Card went into the reader of $machineCode. */
function begin_machine_session(string $machineCode, string $uid): array
{
    close_stale_sessions();
    $machine = find_machine($machineCode);
    if (!$machine) throw new WalletError('Непозната машина ' . $machineCode . '.', 'unknown_machine');
    if (!$machine['active']) throw new WalletError('Машината не е активна.', 'machine_inactive');
    $uid = normalize_uid($uid);
    $card = find_card_by_uid($uid);
    if (!$card) {
        setting('last_unknown_uid', $uid);
        setting('last_unknown_at', utc_now());
        throw new WalletError('Непозната картичка ' . $uid . '.', 'unknown_card');
    }

    tx_begin();
    try {
        $card = lock_card((int)$card['id']);
        $stmt = db()->prepare("SELECT * FROM sessions WHERE status = 'running' AND (machine_id = ? OR card_id = ?)");
        $stmt->execute([$machine['id'], $card['id']]);
        foreach ($stmt->fetchAll() as $open) {
            if ((int)$open['machine_id'] === (int)$machine['id'] && (int)$open['card_id'] === (int)$card['id']) {
                // The reader restarted while the card stayed inside: carry on.
                [$open, $card, $exhausted] = accrue($open, $card, utc_now());
                if ($exhausted) {
                    $open = end_session($open, $card, 'no_balance', 'api');
                    tx_commit();
                    return session_payload($open, $card, false);
                }
                tx_commit();
                return session_payload($open, $card, true);
            }
            // A different card is in this reader now, or this card is still
            // booked on another machine: the old session is over.
            $other = (int)$open['card_id'] === (int)$card['id'] ? $card : lock_card((int)$open['card_id']);
            [$open, $other] = accrue($open, $other, utc_now());
            end_session($open, $other, 'removed', 'api');
            if ((int)$other['id'] === (int)$card['id']) $card = $other;
        }

        if ($card['status'] !== 'active') throw new WalletError('Картичката е блокирана.', 'blocked');
        if ($card['uid'] === DEMO_UID && (int)$card['balance'] < DEMO_BALANCE / 10) {
            $refill = DEMO_BALANCE - (int)$card['balance'];
            $card['balance'] = DEMO_BALANCE;
            db()->prepare('UPDATE cards SET balance = ? WHERE id = ?')->execute([DEMO_BALANCE, $card['id']]);
            insert_ledger((int)$card['id'], 'adjust', $refill, DEMO_BALANCE, ['note' => 'Демо картичка: автоматско полнење', 'source' => 'api']);
        }
        $needed = max((int)$machine['min_balance'], 1);
        if ((int)$card['balance'] < $needed) {
            throw new WalletError('Потребно е најмалку ' . money($needed) . ' · Салдо: ' . money((int)$card['balance']), 'no_balance');
        }
        $now = utc_now();
        db()->prepare(
            'INSERT INTO sessions (card_id, machine_id, rate_per_minute, started_at, last_seen_at) VALUES (?, ?, ?, ?, ?)'
        )->execute([$card['id'], $machine['id'], $machine['price_per_minute'], $now, $now]);
        $session = lock_session((int)db()->lastInsertId());
        tx_commit();
        return session_payload($session, $card, true);
    } catch (Throwable $err) {
        tx_rollback();
        throw $err;
    }
}

/**
 * Reader reports that the card is still inside. $active: foam or water is on right now
 * (null keeps the current state).
 */
function tick_machine_session(int $sessionId, ?bool $active = null): array
{
    tx_begin();
    try {
        $session = lock_session($sessionId);
        $card = lock_card((int)$session['card_id']);
        if ($session['status'] !== 'running') {
            tx_commit();
            return session_payload($session, $card, false);
        }
        $now = utc_now();
        [$session, $card, $exhausted] = accrue($session, $card, $now);
        if ($exhausted || $card['status'] !== 'active') {
            $session = end_session($session, $card, $exhausted ? 'no_balance' : 'admin', 'api');
        } elseif ($active !== null) {
            $session = set_active($session, $active, $now);
        }
        tx_commit();
        return session_payload($session, $card, $session['status'] === 'running');
    } catch (Throwable $err) {
        tx_rollback();
        throw $err;
    }
}

/** Card came out of the reader, the reader went silent, or an admin stopped it. */
function finish_machine_session(int $sessionId, string $reason, string $source, ?int $userId = null): array
{
    tx_begin();
    try {
        $session = lock_session($sessionId);
        $card = lock_card((int)$session['card_id']);
        if ($session['status'] === 'running') {
            $until = $reason === 'timeout' ? $session['last_seen_at'] : utc_now();
            [$session, $card] = accrue($session, $card, $until);
            $session = end_session($session, $card, $reason, $source, $userId);
        }
        tx_commit();
        return session_payload($session, $card, false);
    } catch (Throwable $err) {
        tx_rollback();
        throw $err;
    }
}
