<?php
declare(strict_types=1);
require dirname(__DIR__) . '/app/lib.php';
require dirname(__DIR__) . '/app/layout.php';
$user = require_login();
close_stale_sessions();

if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    check_csrf();
    $sessionId = (int)($_POST['stop_session'] ?? 0);
    if ($sessionId) {
        $done = finish_machine_session($sessionId, 'admin', 'admin', $user['id']);
        flash('Машината е запрена. Наплатено: ' . money($done['charged']));
    }
    redirect('index.php');
}

$machines = db()->query(
    "SELECT m.*, s.id AS session_id, s.started_at, s.last_seen_at, s.charged, c.id AS card_id, c.uid, c.holder_name, c.balance
     FROM machines m
     LEFT JOIN sessions s ON s.machine_id = m.id AND s.status = 'running'
     LEFT JOIN cards c ON c.id = s.card_id
     ORDER BY m.code"
)->fetchAll();

$dayStart = gmdate('Y-m-d H:i:s', strtotime('today'));
$stats = db()->prepare(
    "SELECT
        COALESCE(SUM(CASE WHEN type = 'charge' THEN -amount END), 0) AS spent,
        COALESCE(SUM(CASE WHEN type = 'topup' THEN amount END), 0) AS topups,
        COALESCE(SUM(type = 'charge'), 0) AS cycles
     FROM transactions
     WHERE created_at >= ? AND card_id NOT IN (SELECT id FROM cards WHERE uid = '" . DEMO_UID . "')"
);
$stats->execute([$dayStart]);
$today = $stats->fetch();

$agentSeen = setting('agent_seen_at');
$agentOnline = $agentSeen && time() - utc_ts($agentSeen) < 20;
$statusAt = setting('status_at');
$statusFresh = $agentOnline && $statusAt && time() - utc_ts($statusAt) < 20;
$readerName = (string)setting('reader_name');
$readerOk = $statusFresh && $readerName !== '';
$cardInside = $statusFresh && setting('card_present') === '1';
$plcOk = $statusFresh && setting('plc_ok') === '1';
$plcPeer = (string)setting('plc_peer');
$unknownUid = setting('last_unknown_uid');
$unknownAt = setting('last_unknown_at');
$showUnknown = $unknownUid && $unknownAt && time() - utc_ts($unknownAt) < 3600 && !find_card_by_uid($unknownUid);

page_header('Машини', $user, true);
?>
<div class="stats">
  <div class="stat"><span>Потрошено денес</span><b><?= money((int)$today['spent']) ?></b></div>
  <div class="stat"><span>Наполнето денес</span><b><?= money((int)$today['topups']) ?></b></div>
  <div class="stat"><span>Перења денес</span><b><?= (int)$today['cycles'] ?></b></div>
</div>

<div class="links">
  <div class="link-status <?= $agentOnline ? 'on' : 'off' ?>">
    <i></i>
    <div><b>Агент (лаптоп)</b><small><?= $agentOnline ? 'Поврзан' : ($agentSeen ? 'Не се јавува од ' . local_time($agentSeen, 'H:i:s') : 'Не е стартуван') ?></small></div>
  </div>
  <div class="link-status <?= $readerOk ? 'on' : 'off' ?>">
    <i></i>
    <div>
      <b>Читач на картички</b>
      <small><?= !$statusFresh ? 'Непознато (агентот не се јавува)' : ($readerOk ? 'Поврзан · ' . e($readerName === 'SIMULATION' ? 'симулација' : $readerName) : 'Не е поврзан') ?></small>
      <?php if ($readerOk): ?><small class="<?= $cardInside ? 'card-in' : '' ?>"><?= $cardInside ? '💳 Картичка внатре' : 'Нема картичка' ?></small><?php endif; ?>
    </div>
  </div>
  <div class="link-status <?= $plcOk ? 'on' : 'off' ?>">
    <i></i>
    <div><b>PLC (S7-1200)</b><small><?= !$statusFresh ? 'Непознато (агентот не се јавува)' : ($plcOk ? 'Поврзан · ' . e($plcPeer) : 'Не е поврзан') ?></small></div>
  </div>
</div>

<?php if ($showUnknown): ?>
<div class="flash warn">
  Непозната картичка <b><?= e($unknownUid) ?></b> беше ставена во читачот во <?= local_time($unknownAt, 'H:i') ?>.
  <a class="button" href="cards.php?uid=<?= urlencode($unknownUid) ?>#new">Регистрирај ја</a>
</div>
<?php endif; ?>

<div class="machines">
<?php foreach ($machines as $m): ?>
  <?php $running = !empty($m['session_id']); ?>
  <section class="machine <?= $running ? 'running' : '' ?> <?= $m['active'] ? '' : 'inactive' ?>">
    <header>
      <span class="icon"><?= $m['type'] === 'dryer' ? '🌀' : '🫧' ?></span>
      <div><h2><?= e($m['name']) ?></h2><small><?= e($m['code']) ?> · <?= money((int)$m['price_per_minute']) ?>/мин</small></div>
      <span class="badge"><?= !$m['active'] ? 'Исклучена' : ($running ? 'Работи' : 'Слободна') ?></span>
    </header>
    <?php if ($running): ?>
      <dl>
        <dt>Картичка</dt><dd><a href="card.php?id=<?= (int)$m['card_id'] ?>"><?= e($m['holder_name'] ?: $m['uid']) ?></a></dd>
        <dt>Време</dt><dd><?= duration(time() - utc_ts($m['started_at'])) ?></dd>
        <dt>Наплатено</dt><dd><?= money((int)$m['charged']) ?></dd>
        <dt>Салдо</dt><dd><?= money((int)$m['balance']) ?> · уште ~<?= duration(seconds_left((int)$m['balance'], (int)$m['price_per_minute'])) ?></dd>
      </dl>
      <form method="post" onsubmit="return confirm('Да ја запрам машината?')">
        <?= csrf_field() ?>
        <button name="stop_session" value="<?= (int)$m['session_id'] ?>" class="danger">Запри</button>
      </form>
    <?php else: ?>
      <p class="muted">Стави картичка во читачот за да почне.</p>
    <?php endif; ?>
  </section>
<?php endforeach; ?>
</div>
<?php page_footer();
