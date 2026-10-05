<?php
// PLC link settings: how the laptop and the S7-1200 talk Modbus TCP.
// Saved in the database; the agent picks them up within a few seconds (no restart).
declare(strict_types=1);
require dirname(__DIR__) . '/app/lib.php';
require dirname(__DIR__) . '/app/layout.php';
$user = require_login();

function link_status(): array
{
    $agentSeen = setting('agent_seen_at');
    $statusAt = setting('status_at');
    $fresh = $agentSeen && $statusAt && time() - utc_ts($agentSeen) < 20 && time() - utc_ts($statusAt) < 20;
    return [
        'fresh' => (bool)$fresh,
        'ok' => $fresh && setting('plc_ok') === '1',
        'peer' => (string)setting('plc_peer'),
        'link' => (string)setting('plc_link'),
        'error' => (string)setting('plc_error'),
        'local_ips' => array_values(array_filter(explode(',', (string)setting('local_ips')))),
        'version' => (string)setting('agent_version'),
    ];
}

// Polled by the page every 2 s so the status is live without reloading the form.
if (($_GET['status'] ?? '') === '1') {
    header('Content-Type: application/json; charset=utf-8');
    header('Cache-Control: no-store');
    echo json_encode(link_status(), JSON_UNESCAPED_UNICODE);
    exit;
}

$form = plc_settings();
if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    check_csrf();
    $errors = save_plc_settings($_POST);
    if (!$errors) {
        flash('Поставките се зачувани. Агентот ги применува за неколку секунди.');
        redirect('plc.php');
    }
    flash(implode(' ', $errors), 'error');
    $form = array_merge($form, array_intersect_key($_POST, PLC_DEFAULTS));
    $form['test_mode'] = !empty($_POST['test_mode']) ? '1' : '0';
}

$st = link_status();
$ips = $st['local_ips'];
$laptopIp = $ips[0] ?? '192.168.1.10';
page_header('PLC врска', $user);
?>
<h1>PLC врска (Modbus TCP)</h1>

<section class="card plc-live <?= $st['ok'] ? 'on' : 'off' ?>" id="plcLive">
  <div class="row between">
    <div>
      <b id="liveTitle"><?= !$st['fresh'] ? 'Агентот не се јавува' : ($st['ok'] ? 'PLC е поврзан' : 'PLC не е поврзан') ?></b>
      <div class="muted" id="liveLink"><?= e($st['link']) ?><?= $st['ok'] && $st['peer'] ? ' · ' . e($st['peer']) : '' ?></div>
      <div class="plc-error" id="liveError"><?= $st['fresh'] && !$st['ok'] ? e($st['error']) : '' ?></div>
    </div>
    <div class="muted small" id="liveIps"><?= $ips ? 'IP на овој лаптоп: ' . e(implode(', ', $ips)) : '' ?></div>
  </div>
</section>

<form method="post" class="card plc-form">
  <?= csrf_field() ?>
  <h2>Кој кого повикува?</h2>
  <div class="mode-pick">
    <label class="mode-option">
      <input type="radio" name="plc_mode" value="server" <?= $form['plc_mode'] !== 'client' ? 'checked' : '' ?>>
      <span><b>Лаптопот е сервер</b><small>PLC-то се поврзува на лаптопот (MB_CLIENT). Програма: <code>plc\FB_Peralna.scl</code>. IP адресата на лаптопот се пишува во PLC програмата.</small></span>
    </label>
    <label class="mode-option">
      <input type="radio" name="plc_mode" value="client" <?= $form['plc_mode'] === 'client' ? 'checked' : '' ?>>
      <span><b>PLC-то е сервер</b><small>Лаптопот се поврзува на PLC-то (MB_SERVER). Програма: <code>plc\FB_Peralna_Server.scl</code>. Во PLC програмата не се пишува ништо за лаптопот.</small></span>
    </label>
  </div>

  <fieldset class="mode-fields" data-mode="server">
    <legend>Лаптопот е сервер</legend>
    <div class="grid">
      <label>IP на која слуша лаптопот
        <input name="plc_listen_ip" value="<?= e($form['plc_listen_ip']) ?>" placeholder="0.0.0.0" inputmode="decimal">
        <small>0.0.0.0 = на сите мрежни картички</small>
      </label>
      <label>Порта
        <input name="plc_listen_port" value="<?= e($form['plc_listen_port']) ?>" placeholder="502" inputmode="numeric">
        <small>Стандардно 502</small>
      </label>
      <label>Дозволена IP на PLC-то
        <input name="plc_allowed_ip" value="<?= e($form['plc_allowed_ip']) ?>" placeholder="празно = било која" inputmode="decimal">
        <small>Ако е пополнета, само тоа PLC смее да се поврзе</small>
      </label>
    </div>
    <p class="muted">Во <code>FB_Peralna.scl</code> внеси ја IP адресата на лаптопот (<b><?= e($laptopIp) ?></b>) во четирите <code>ADDR</code> линии и портата во <code>RemotePort</code>.</p>
  </fieldset>

  <fieldset class="mode-fields" data-mode="client">
    <legend>PLC-то е сервер</legend>
    <div class="grid">
      <label>IP на PLC-то
        <input name="plc_ip" value="<?= e($form['plc_ip']) ?>" placeholder="192.168.1.20" inputmode="decimal">
        <small>Од TIA Portal → PROFINET интерфејс</small>
      </label>
      <label>Порта на PLC-то
        <input name="plc_port" value="<?= e($form['plc_port']) ?>" placeholder="502" inputmode="numeric">
        <small>Иста како <code>LocalPort</code> во програмата</small>
      </label>
      <label>Unit ID
        <input name="plc_unit_id" value="<?= e($form['plc_unit_id']) ?>" placeholder="1" inputmode="numeric">
        <small>S7-1200 MB_SERVER прифаќа било кој</small>
      </label>
      <label>Освежување (ms)
        <input name="plc_poll_ms" value="<?= e($form['plc_poll_ms']) ?>" placeholder="200" inputmode="numeric">
        <small>50–2000, стандардно 200</small>
      </label>
    </div>
  </fieldset>

  <h2>Тест режим</h2>
  <label class="check">
    <input type="checkbox" name="test_mode" value="1" <?= $form['test_mode'] === '1' ? 'checked' : '' ?>>
    Вклучи ги емулаторите и демо картичката (само за проба, не во работа)
  </label>
  <p class="muted">Исклучено: агентот работи само со вистинскиот читач и вистинското PLC. Демо картичката <code><?= e(DEMO_UID) ?></code> се одбива.</p>

  <button class="primary">Зачувај</button>
</form>

<script>
(function () {
  var radios = document.querySelectorAll('input[name="plc_mode"]');
  function show() {
    var mode = document.querySelector('input[name="plc_mode"]:checked').value;
    document.querySelectorAll('.mode-fields').forEach(function (f) { f.hidden = f.dataset.mode !== mode; });
  }
  radios.forEach(function (r) { r.addEventListener('change', show); });
  show();

  var box = document.getElementById('plcLive');
  function poll() {
    fetch('plc.php?status=1', { cache: 'no-store' }).then(function (r) { return r.json(); }).then(function (s) {
      box.className = 'card plc-live ' + (s.ok ? 'on' : 'off');
      document.getElementById('liveTitle').textContent = !s.fresh ? 'Агентот не се јавува' : (s.ok ? 'PLC е поврзан' : 'PLC не е поврзан');
      document.getElementById('liveLink').textContent = s.link + (s.ok && s.peer ? ' · ' + s.peer : '');
      document.getElementById('liveError').textContent = s.fresh && !s.ok ? s.error : '';
      document.getElementById('liveIps').textContent = s.local_ips.length ? 'IP на овој лаптоп: ' + s.local_ips.join(', ') : '';
    }).catch(function () {});
  }
  setInterval(poll, 2000);
})();
</script>
<?php page_footer();
