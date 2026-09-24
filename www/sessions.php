<?php
declare(strict_types=1);
require dirname(__DIR__) . '/app/lib.php';
require dirname(__DIR__) . '/app/layout.php';
$user = require_login();
close_stale_sessions();

$from = preg_match('/^\d{4}-\d{2}-\d{2}$/', (string)($_GET['from'] ?? '')) ? $_GET['from'] : date('Y-m-d', strtotime('-6 days'));
$to = preg_match('/^\d{4}-\d{2}-\d{2}$/', (string)($_GET['to'] ?? '')) ? $_GET['to'] : date('Y-m-d');
$fromUtc = gmdate('Y-m-d H:i:s', strtotime($from . ' 00:00:00'));
$toUtc = gmdate('Y-m-d H:i:s', strtotime($to . ' 23:59:59'));

$stmt = db()->prepare(
    'SELECT s.*, m.name AS machine_name, c.uid, c.holder_name
     FROM sessions s JOIN machines m ON m.id = s.machine_id JOIN cards c ON c.id = s.card_id
     WHERE s.started_at BETWEEN ? AND ? ORDER BY s.id DESC LIMIT 1000'
);
$stmt->execute([$fromUtc, $toUtc]);
$sessions = $stmt->fetchAll();
$total = array_sum(array_map(fn($s) => (int)$s['charged'], $sessions));

page_header('Перења', $user);
?>
<div class="row between">
  <h1>Перења</h1>
  <form method="get" class="search">
    <input type="date" name="from" value="<?= e($from) ?>"> – <input type="date" name="to" value="<?= e($to) ?>">
    <button>Прикажи</button>
  </form>
</div>
<p class="muted"><?= count($sessions) ?> перења · вкупно наплатено <b><?= money($total) ?></b></p>
<table>
  <thead><tr><th>Почеток</th><th>Машина</th><th>Картичка</th><th>Траење</th><th class="num">Наплатено</th><th>Крај</th></tr></thead>
  <tbody>
  <?php foreach ($sessions as $s): ?>
    <?php $end = $s['ended_at'] ? utc_ts($s['ended_at']) : time(); ?>
    <tr>
      <td><?= local_time($s['started_at']) ?></td>
      <td><?= e($s['machine_name']) ?></td>
      <td><a href="card.php?id=<?= (int)$s['card_id'] ?>"><?= e($s['holder_name'] ?: $s['uid']) ?></a></td>
      <td><?= duration($end - utc_ts($s['started_at'])) ?></td>
      <td class="num"><?= money((int)$s['charged']) ?></td>
      <td><?= $s['status'] === 'running' ? '<span class="pill ok">Работи</span>' : e(END_REASONS[$s['end_reason']] ?? '') ?></td>
    </tr>
  <?php endforeach; ?>
  <?php if (!$sessions): ?><tr><td colspan="6" class="muted">Нема перења во овој период.</td></tr><?php endif; ?>
  </tbody>
</table>
<?php page_footer();
