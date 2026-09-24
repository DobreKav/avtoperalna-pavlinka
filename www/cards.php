<?php
declare(strict_types=1);
require dirname(__DIR__) . '/app/lib.php';
require dirname(__DIR__) . '/app/layout.php';
$user = require_login();

if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    check_csrf();
    $uid = normalize_uid((string)($_POST['uid'] ?? ''));
    $initial = max(0, (int)($_POST['initial'] ?? 0));
    if (strlen($uid) < 4) {
        flash('Внеси го бројот (UID) на картичката.', 'error');
        redirect('cards.php#new');
    }
    if (find_card_by_uid($uid)) {
        flash('Картичката ' . $uid . ' веќе постои.', 'error');
        redirect('cards.php#new');
    }
    db()->prepare('INSERT INTO cards (uid, holder_name, phone, created_at) VALUES (?, ?, ?, ?)')->execute([
        $uid,
        mb_substr(trim((string)($_POST['holder_name'] ?? '')), 0, 120),
        mb_substr(trim((string)($_POST['phone'] ?? '')), 0, 40),
        utc_now(),
    ]);
    $id = (int)db()->lastInsertId();
    if ($initial > 0) {
        apply_transaction($id, 'topup', $initial, ['note' => 'Почетно полнење', 'user_id' => $user['id']]);
    }
    flash('Картичката е регистрирана.');
    redirect('card.php?id=' . $id);
}

$q = trim((string)($_GET['q'] ?? ''));
$sql = 'SELECT * FROM cards';
$params = [];
if ($q !== '') {
    $sql .= ' WHERE uid LIKE ? OR holder_name LIKE ? OR phone LIKE ?';
    $like = '%' . $q . '%';
    $params = [$like, $like, $like];
}
$stmt = db()->prepare($sql . ' ORDER BY created_at DESC LIMIT 300');
$stmt->execute($params);
$cards = $stmt->fetchAll();
$totalBalance = (int)db()->query('SELECT COALESCE(SUM(balance), 0) FROM cards')->fetchColumn();
$prefillUid = normalize_uid((string)($_GET['uid'] ?? ''));

page_header('Картички', $user);
?>
<div class="row between">
  <h1>Картички</h1>
  <form method="get" class="search"><input name="q" value="<?= e($q) ?>" placeholder="Барај по име, телефон или UID"><button>Барај</button></form>
</div>
<p class="muted">Вкупно салдо на сите картички: <b><?= money($totalBalance) ?></b></p>

<table>
  <thead><tr><th>UID</th><th>Име</th><th>Телефон</th><th class="num">Салдо</th><th>Статус</th></tr></thead>
  <tbody>
  <?php foreach ($cards as $c): ?>
    <tr onclick="location='card.php?id=<?= (int)$c['id'] ?>'" class="clickable">
      <td><code><?= e($c['uid']) ?></code></td>
      <td><a href="card.php?id=<?= (int)$c['id'] ?>"><?= e($c['holder_name'] ?: '—') ?></a></td>
      <td><?= e($c['phone']) ?></td>
      <td class="num"><?= money((int)$c['balance']) ?></td>
      <td><?= $c['status'] === 'active' ? '<span class="pill ok">Активна</span>' : '<span class="pill bad">Блокирана</span>' ?></td>
    </tr>
  <?php endforeach; ?>
  <?php if (!$cards): ?><tr><td colspan="5" class="muted">Нема картички.</td></tr><?php endif; ?>
  </tbody>
</table>

<section class="card" id="new">
  <h2>Нова картичка</h2>
  <p class="muted">Стави ја картичката во читачот: бројот ќе се појави на почетната страница како „непозната картичка“. Можеш и рачно да го внесеш.</p>
  <form method="post" class="grid">
    <?= csrf_field() ?>
    <label>UID <input name="uid" required value="<?= e($prefillUid) ?>" placeholder="04A1B2C3" <?= $prefillUid ? '' : 'autofocus' ?>></label>
    <label>Име и презиме <input name="holder_name" <?= $prefillUid ? 'autofocus' : '' ?>></label>
    <label>Телефон <input name="phone"></label>
    <label>Почетно полнење (<?= e(config('currency')) ?>) <input type="number" name="initial" min="0" step="1" value="0"></label>
    <button class="primary">Регистрирај</button>
  </form>
</section>
<?php page_footer();
