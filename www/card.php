<?php
declare(strict_types=1);
require dirname(__DIR__) . '/app/lib.php';
require dirname(__DIR__) . '/app/layout.php';
$user = require_login();

$id = (int)($_GET['id'] ?? 0);
$stmt = db()->prepare('SELECT * FROM cards WHERE id = ?');
$stmt->execute([$id]);
$card = $stmt->fetch();
if (!$card) {
    flash('Картичката не постои.', 'error');
    redirect('cards.php');
}

if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    check_csrf();
    $action = (string)($_POST['action'] ?? '');
    try {
        if ($action === 'topup') {
            $amount = (int)($_POST['amount'] ?? 0);
            if ($amount <= 0) throw new WalletError('Внеси позитивен износ.');
            $balance = apply_transaction($id, 'topup', $amount, ['note' => trim((string)($_POST['note'] ?? '')), 'user_id' => $user['id']]);
            flash('Наполнето ' . money($amount) . ' · Ново салдо: ' . money($balance));
        } elseif ($action === 'adjust') {
            $amount = (int)($_POST['amount'] ?? 0);
            $note = trim((string)($_POST['note'] ?? ''));
            if ($note === '') throw new WalletError('За корекција напиши причина.');
            $type = ($_POST['kind'] ?? '') === 'refund' ? 'refund' : 'adjust';
            $balance = apply_transaction($id, $type, $amount, ['note' => $note, 'user_id' => $user['id']]);
            flash('Салдото е корегирано. Ново салдо: ' . money($balance));
        } elseif ($action === 'edit') {
            db()->prepare('UPDATE cards SET holder_name = ?, phone = ? WHERE id = ?')->execute([
                mb_substr(trim((string)($_POST['holder_name'] ?? '')), 0, 120),
                mb_substr(trim((string)($_POST['phone'] ?? '')), 0, 40),
                $id,
            ]);
            flash('Податоците се зачувани.');
        } elseif ($action === 'toggle') {
            $status = $card['status'] === 'active' ? 'blocked' : 'active';
            db()->prepare('UPDATE cards SET status = ? WHERE id = ?')->execute([$status, $id]);
            flash($status === 'blocked' ? 'Картичката е блокирана.' : 'Картичката е активна.');
        }
    } catch (WalletError $err) {
        flash($err->getMessage(), 'error');
    }
    redirect('card.php?id=' . $id);
}

$stmt = db()->prepare(
    'SELECT t.*, m.name AS machine_name, u.username
     FROM transactions t
     LEFT JOIN machines m ON m.id = t.machine_id
     LEFT JOIN users u ON u.id = t.user_id
     WHERE t.card_id = ? ORDER BY t.id DESC LIMIT 200'
);
$stmt->execute([$id]);
$history = $stmt->fetchAll();

$stmt = db()->prepare(
    "SELECT s.*, m.name AS machine_name FROM sessions s JOIN machines m ON m.id = s.machine_id
     WHERE s.card_id = ? AND s.status = 'running'"
);
$stmt->execute([$id]);
$running = $stmt->fetch();

page_header($card['holder_name'] ?: $card['uid'], $user);
?>
<div class="row between">
  <div>
    <h1><?= e($card['holder_name'] ?: 'Картичка без име') ?></h1>
    <p class="muted"><code><?= e($card['uid']) ?></code> · <?= e($card['phone']) ?> · од <?= local_time($card['created_at'], 'd.m.Y') ?></p>
  </div>
  <div class="balance <?= $card['status'] === 'active' ? '' : 'blocked' ?>">
    <span>Салдо</span><b><?= money((int)$card['balance']) ?></b>
    <?php if ($card['status'] !== 'active'): ?><small>БЛОКИРАНА</small><?php endif; ?>
  </div>
</div>

<?php if ($running): ?>
<div class="flash warn">Во моментов е во <b><?= e($running['machine_name']) ?></b> од <?= local_time($running['started_at'], 'H:i') ?> · наплатено <?= money((int)$running['charged']) ?></div>
<?php endif; ?>

<div class="columns">
  <section class="card">
    <h2>Полнење</h2>
    <form method="post" class="presets">
      <?= csrf_field() ?><input type="hidden" name="action" value="topup">
      <?php foreach (config('topup_presets') as $p): ?>
        <button name="amount" value="<?= (int)$p ?>" class="primary">+<?= money((int)$p) ?></button>
      <?php endforeach; ?>
    </form>
    <form method="post" class="inline">
      <?= csrf_field() ?><input type="hidden" name="action" value="topup">
      <input type="number" name="amount" min="1" step="1" placeholder="Друг износ" required>
      <input name="note" placeholder="Белешка (опционално)">
      <button class="primary">Наполни</button>
    </form>
  </section>

  <section class="card">
    <h2>Корекција / враќање</h2>
    <form method="post" class="stack">
      <?= csrf_field() ?><input type="hidden" name="action" value="adjust">
      <label>Износ (минус одзема) <input type="number" name="amount" step="1" required></label>
      <label>Тип <select name="kind"><option value="adjust">Корекција</option><option value="refund">Враќање (машината не работела)</option></select></label>
      <label>Причина <input name="note" required></label>
      <button>Зачувај</button>
    </form>
  </section>

  <section class="card">
    <h2>Податоци</h2>
    <form method="post" class="stack">
      <?= csrf_field() ?><input type="hidden" name="action" value="edit">
      <label>Име и презиме <input name="holder_name" value="<?= e($card['holder_name']) ?>"></label>
      <label>Телефон <input name="phone" value="<?= e($card['phone']) ?>"></label>
      <button>Зачувај</button>
    </form>
    <form method="post" onsubmit="return confirm('Сигурно?')">
      <?= csrf_field() ?><input type="hidden" name="action" value="toggle">
      <button class="<?= $card['status'] === 'active' ? 'danger' : 'primary' ?>"><?= $card['status'] === 'active' ? 'Блокирај (изгубена)' : 'Одблокирај' ?></button>
    </form>
  </section>
</div>

<h2>Историја</h2>
<table>
  <thead><tr><th>Време</th><th>Тип</th><th>Машина / белешка</th><th class="num">Износ</th><th class="num">Салдо после</th><th>Од</th></tr></thead>
  <tbody>
  <?php foreach ($history as $t): ?>
    <tr>
      <td><?= local_time($t['created_at']) ?></td>
      <td><?= e(TX_LABELS[$t['type']] ?? $t['type']) ?></td>
      <td><?= e(trim(($t['machine_name'] ?? '') . ' ' . $t['note'])) ?></td>
      <td class="num <?= (int)$t['amount'] < 0 ? 'neg' : 'pos' ?>"><?= ((int)$t['amount'] > 0 ? '+' : '') . money((int)$t['amount']) ?></td>
      <td class="num"><?= money((int)$t['balance_after']) ?></td>
      <td><?= e($t['username'] ?? ($t['source'] === 'api' ? 'читач' : $t['source'])) ?></td>
    </tr>
  <?php endforeach; ?>
  <?php if (!$history): ?><tr><td colspan="6" class="muted">Нема трансакции.</td></tr><?php endif; ?>
  </tbody>
</table>
<?php page_footer();
