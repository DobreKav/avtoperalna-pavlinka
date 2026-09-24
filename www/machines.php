<?php
declare(strict_types=1);
require dirname(__DIR__) . '/app/lib.php';
require dirname(__DIR__) . '/app/layout.php';
$user = require_login();

if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    check_csrf();
    $code = strtoupper(preg_replace('/[^0-9A-Za-z_-]/', '', (string)($_POST['code'] ?? '')) ?? '');
    $name = mb_substr(trim((string)($_POST['name'] ?? '')), 0, 80);
    $type = ($_POST['type'] ?? '') === 'dryer' ? 'dryer' : 'washer';
    $rate = (int)($_POST['price_per_minute'] ?? 0);
    $min = max(0, (int)($_POST['min_balance'] ?? 0));
    $active = isset($_POST['active']) ? 1 : 0;
    $id = (int)($_POST['id'] ?? 0);
    if ($code === '' || $name === '' || $rate <= 0) {
        flash('Внеси код, име и цена по минута поголема од 0.', 'error');
    } else {
        try {
            if ($id) {
                // A running session keeps the rate it started with.
                db()->prepare('UPDATE machines SET code = ?, name = ?, type = ?, price_per_minute = ?, min_balance = ?, active = ? WHERE id = ?')
                    ->execute([$code, $name, $type, $rate, $min, $active, $id]);
                flash('Машината е зачувана.');
            } else {
                db()->prepare('INSERT INTO machines (code, name, type, price_per_minute, min_balance, active) VALUES (?, ?, ?, ?, ?, ?)')
                    ->execute([$code, $name, $type, $rate, $min, $active]);
                flash('Машината е додадена.');
            }
        } catch (PDOException $err) {
            flash('Кодот ' . $code . ' веќе постои.', 'error');
        }
    }
    redirect('machines.php');
}

$machines = db()->query('SELECT * FROM machines ORDER BY code')->fetchAll();
page_header('Машини и цени', $user);

function machine_form(?array $m): void
{
    ?>
    <form method="post" class="machine-row">
      <?= csrf_field() ?>
      <input type="hidden" name="id" value="<?= (int)($m['id'] ?? 0) ?>">
      <label>Код <input name="code" value="<?= e($m['code'] ?? '') ?>" required size="5"></label>
      <label>Име <input name="name" value="<?= e($m['name'] ?? '') ?>" required></label>
      <label>Тип
        <select name="type">
          <option value="washer" <?= ($m['type'] ?? '') === 'washer' ? 'selected' : '' ?>>Перална</option>
          <option value="dryer" <?= ($m['type'] ?? '') === 'dryer' ? 'selected' : '' ?>>Сушара</option>
        </select>
      </label>
      <label>Цена/мин <input type="number" name="price_per_minute" min="1" value="<?= (int)($m['price_per_minute'] ?? 5) ?>" required></label>
      <label>Мин. салдо за старт <input type="number" name="min_balance" min="0" value="<?= (int)($m['min_balance'] ?? 50) ?>"></label>
      <label class="check"><input type="checkbox" name="active" <?= ($m['active'] ?? 1) ? 'checked' : '' ?>> Активна</label>
      <button class="primary"><?= $m ? 'Зачувај' : 'Додади' ?></button>
    </form>
    <?php
}
?>
<h1>Машини и цени</h1>
<p class="muted">Кодот мора да е ист како <code>machine</code> во <code>agent\agent.ini</code> на читачот. Картичката плаќа по минута додека е во читачот; за да почне, треба да има барем „мин. салдо“.</p>
<section class="card">
  <?php foreach ($machines as $m) machine_form($m); ?>
</section>
<section class="card">
  <h2>Нова машина</h2>
  <?php machine_form(null); ?>
</section>
<?php page_footer();
