'use strict';

window.deskWidgets = (() => {
  const metrics = {
    'cpu.usage': ['CPU 占用', '%', 100],
    'cpu.temperature': ['CPU 温度', '°C', 100],
    'gpu.usage': ['GPU 占用', '%', 100],
    'gpu.temperature': ['GPU 温度', '°C', 100],
    'gpu.power': ['GPU 功耗', 'W', 150],
    'ram.used': ['内存已用', 'GB', 'ram.total'],
    'ram.total': ['内存总量', 'GB', 'ram.total'],
    'ram.usage': ['内存占用', '%', 100],
    'network.downMBs': ['下载速度', 'MB/s', 20],
    'network.upMBs': ['上传速度', 'MB/s', 20]
  };
  const timeFormat = new Intl.DateTimeFormat('zh-CN', { hour: '2-digit', minute: '2-digit', hourCycle: 'h23' });
  const dateFormat = new Intl.DateTimeFormat('zh-CN', { month: 'long', day: 'numeric', weekday: 'short' });

  function node(tag, className, text) {
    const element = document.createElement(tag);
    if (className) element.className = className;
    if (text != null) element.textContent = text;
    return element;
  }

  function clock(ms) {
    if (!Number.isFinite(ms)) return '—';
    const seconds = Math.max(0, Math.floor(ms / 1000));
    return Math.floor(seconds / 60) + ':' + String(seconds % 60).padStart(2, '0');
  }

  function replaceText(element, text) {
    if (element.textContent !== text) element.textContent = text;
  }

  function metricValue(id, values) {
    const amount = values[id];
    if (!Number.isFinite(amount)) return '—';
    const [, unit] = metrics[id];
    if (unit === 'MB/s') {
      const rate = rateParts(amount);
      return rate.unit ? rate.value + ' ' + rate.unit : rate.value;
    }
    const rounded = unit === 'GB' ? Math.round(amount * 10) / 10 : Math.round(amount);
    return rounded + (unit === '%' || unit === '°C' ? '' : ' ') + unit;
  }

  function rateParts(value) {
    if (!Number.isFinite(value)) return { value: '—', unit: '' };
    if (value < 1) {
      const kb = value * 1000;
      return {
        value: kb < .05 ? '0' : kb < 10 ? kb.toFixed(1) : String(Math.round(kb)),
        unit: 'KB/s'
      };
    }
    return {
      value: value >= 100 ? String(Math.round(value)) : value.toFixed(1),
      unit: 'MB/s'
    };
  }

  function metricDetail(id, values) {
    const available = (keys) => keys.filter(key => Number.isFinite(values[key])).map(key => metricValue(key, values));
    switch (id) {
      case 'cpu.usage':
        return available(['cpu.temperature']).join('') || '温度未提供';
      case 'gpu.usage':
        return available(['gpu.temperature', 'gpu.power']).join(' · ') || '传感器未提供';
      case 'ram.used': {
        const usage = Number.isFinite(values['ram.usage']) ? Math.round(values['ram.usage']) + '%' : null;
        const total = Number.isFinite(values['ram.total']) ? '共 ' + metricValue('ram.total', values) : '物理内存';
        return [usage, total].filter(Boolean).join(' · ');
      }
      case 'gpu.temperature': {
        const [power] = available(['gpu.power']);
        const [usage] = available(['gpu.usage']);
        return [power && '功耗 ' + power, usage && '占用 ' + usage].filter(Boolean).join(' · ') || '核心温度';
      }
      case 'network.downMBs':
        return '上传 ' + metricValue('network.upMBs', values);
      case 'network.upMBs':
        return '下载 ' + metricValue('network.downMBs', values);
      default:
        return Number.isFinite(values[id]) ? metrics[id][0] : '读数未提供';
    }
  }

  function formatTemplate(template, context) {
    const state = context.state;
    const now = context.now ?? new Date();
    const values = context.hardware?.values ?? {};
    const tokens = {
      time: timeFormat.format(now), date: dateFormat.format(now),
      'music.title': state?.song?.title || '未播放',
      'music.artist': state?.song?.artist || '—',
      'music.progress': clock(state?.positionMs),
      'music.duration': clock(state?.durationMs),
      'gpu.name': context.hardware?.gpuName ?? '—'
    };
    return template.replace(/\{\{([a-z][a-z.]*)\}\}/g, (_, key) =>
      Object.hasOwn(metrics, key) ? metricValue(key, values) : tokens[key] ?? '—');
  }

  function make(widget, press, mediaControl, openNote) {
    if (widget.type === 'music') return makeMusic(widget, mediaControl);
    if (widget.type === 'shortcut') return makeShortcut(widget, press);
    if (widget.type === 'image' || widget.type === 'gif') return makeImage(widget);
    if (widget.type === 'emoji') return makeEmoji(widget);
    if (widget.type === 'network') return makeNetwork(widget);
    if (widget.type === 'codex') return makeCodex(widget);
    const metric = widget.type === 'metric' ? widget.metric :
      widget.type === 'custom' ? widget.manifest?.gauge : null;
    const note = widget.type === 'text';
    const tile = node(note && openNote ? 'button' : 'article', 'widget widget-' + widget.size);
    if (note) tile.classList.add('widget-note');
    if (note && openNote) {
      tile.type = 'button';
      tile.title = '在 Windows 桌面新建便签并打开';
      tile.addEventListener('click', async () => {
        tile.disabled = true;
        try { await openNote(widget); }
        finally { tile.disabled = false; }
      });
    }
    let dial;
    if (widget.type === 'clock') {
      tile.classList.add('widget-clock');
      dial = node('span', 'widget-clock-dial');
      dial.setAttribute('aria-hidden', 'true');
      tile.append(dial);
    }
    if (note || widget.type === 'date') {
      if (!note) tile.classList.add('widget-date');
      const paper = node('span', 'widget-paper-icon');
      paper.setAttribute('aria-hidden', 'true');
      tile.append(paper);
    }
    tile.style.setProperty('--widget-accent', widget.color);
    const value = node('span', 'widget-value');
    const detail = node('span', 'widget-detail');
    const face = node('span', metric ? 'widget-gauge' : 'widget-face');
    if (widget.type === 'metric') face.dataset.metric = metric;
    if (metric) tile.classList.add('widget-with-gauge');
    face.append(value);
    tile.append(face);
    if (widget.name.trim()) tile.append(node('span', 'widget-label', widget.name));
    tile.append(detail);

    function update(context) {
      const hw = context.hardware?.values ?? {};
      const now = context.now ?? new Date();
      let main = '';
      let sub = '';
      switch (widget.type) {
        case 'metric':
          main = metricValue(metric, hw);
          if (main !== value.textContent) value.style.setProperty('--reading-length', main.length);
          sub = metricDetail(metric, hw);
          break;
        case 'clock':
          main = timeFormat.format(now);
          sub = dateFormat.format(now);
          if (value.textContent !== main) {
            const minutes = now.getMinutes();
            dial.style.setProperty('--hour-angle', (now.getHours() % 12 * 30 + minutes / 2) + 'deg');
            dial.style.setProperty('--minute-angle', minutes * 6 + 'deg');
          }
          break;
        case 'date':
          main = dateFormat.format(now);
          sub = String(now.getFullYear());
          break;
        case 'text':
          main = widget.text;
          break;
        case 'custom':
          main = formatTemplate(widget.manifest.template, context);
          sub = widget.manifest.detail ? formatTemplate(widget.manifest.detail, context) : '';
          break;
      }
      replaceText(value, main);
      replaceText(detail, sub);
      if (metric) {
        const maximum = metrics[metric][2];
        const divisor = typeof maximum === 'number' ? maximum : hw[maximum];
        const ratio = Number.isFinite(hw[metric]) && divisor > 0 ? Math.min(1, Math.max(0, hw[metric] / divisor)) : 0;
        face.style.setProperty('--widget-progress', Math.round(ratio * 1000) / 10 + '%');
      }
    }
    return { element: tile, update };
  }

  function makeImage(widget) {
    const tile = node('article', 'widget widget-' + widget.size + ' widget-image-card');
    tile.style.setProperty('--widget-accent', widget.color);
    const image = node('img', 'widget-card-image');
    image.alt = widget.name.trim() || (widget.type === 'gif' ? 'GIF 动图' : '自选图片');
    image.style.objectFit = widget.fit ?? 'contain';
    image.hidden = true;
    const empty = node('span', 'widget-card-image-empty', '—');
    empty.setAttribute('aria-label', widget.type === 'gif' ? '暂无 GIF' : '暂无图片');
    image.addEventListener('load', () => { image.hidden = false; empty.hidden = true; });
    image.addEventListener('error', () => { image.hidden = true; empty.hidden = false; });
    if (widget.mediaId) image.src = '/media/widgets/' + encodeURIComponent(widget.mediaId);
    tile.append(empty, image);
    if (widget.name.trim()) tile.append(node('span', 'widget-label', widget.name));
    return { element: tile, update() {} };
  }

  function makeEmoji(widget) {
    const tile = node('article', 'widget widget-' + widget.size + ' widget-emoji');
    tile.style.setProperty('--widget-accent', widget.color);
    tile.append(node('span', 'widget-emoji-value', widget.text));
    if (widget.name.trim()) tile.append(node('span', 'widget-label', widget.name));
    return { element: tile, update() {} };
  }

  function makeNetwork(widget) {
    const tile = node('article', 'widget widget-' + widget.size + ' widget-network');
    tile.style.setProperty('--widget-accent', widget.color);
    const dial = node('div', 'network-dial');
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('class', 'network-arcs');
    svg.setAttribute('viewBox', '0 0 100 100');
    svg.setAttribute('aria-hidden', 'true');
    const path = (className, definition) => {
      const element = document.createElementNS('http://www.w3.org/2000/svg', 'path');
      element.setAttribute('class', className);
      element.setAttribute('d', definition);
      return element;
    };
    const left = 'M50 6 A44 44 0 0 0 50 94';
    const right = 'M50 94 A44 44 0 0 0 50 6';
    svg.append(path('network-arc-track', left), path('network-arc-track', right));
    const downPath = path('network-arc network-download', left);
    const upPath = path('network-arc network-upload', right);
    for (const arc of [downPath, upPath]) {
      arc.setAttribute('pathLength', '1');
      arc.setAttribute('stroke-dasharray', '1');
      arc.setAttribute('stroke-dashoffset', '1');
    }
    svg.append(downPath, upPath);
    dial.append(svg);
    const reading = (direction, label) => {
      const cell = node('div', 'network-side');
      const line = node('div', 'network-reading');
      line.append(node('span', 'network-arrow', direction === 'down' ? '↓' : '↑'));
      const value = node('span', 'network-value');
      const unit = node('span', 'network-unit', 'MB/s');
      line.append(value, unit);
      cell.append(line, node('span', 'network-label', label));
      return { cell, value, unit };
    };
    const down = reading('down', '下载');
    const up = reading('up', '上传');
    dial.append(down.cell, up.cell);
    dial.setAttribute('aria-label', '实时网速');
    tile.append(dial);
    if (widget.name.trim()) tile.append(node('span', 'widget-label', widget.name));
    function update(context) {
      const values = context.hardware?.values ?? {};
      const downRate = rateParts(values['network.downMBs']);
      const upRate = rateParts(values['network.upMBs']);
      replaceText(down.value, downRate.value);
      replaceText(down.unit, downRate.unit);
      replaceText(up.value, upRate.value);
      replaceText(up.unit, upRate.unit);
      const downRatio = Math.min(1, Math.max(0, (values['network.downMBs'] ?? 0) / 20));
      const upRatio = Math.min(1, Math.max(0, (values['network.upMBs'] ?? 0) / 20));
      downPath.style.strokeDashoffset = String(1 - downRatio);
      upPath.style.strokeDashoffset = String(1 - upRatio);
    }
    return { element: tile, update };
  }

  function makeCodex(widget) {
    const tile = node('article', 'widget widget-' + widget.size + ' widget-codex');
    tile.style.setProperty('--widget-accent', widget.color);
    if (widget.name.trim()) tile.append(node('span', 'widget-label', widget.name));
    const list = node('div', 'codex-list');
    const empty = node('span', 'codex-empty', '暂无 AI 任务');
    const more = node('span', 'codex-more');
    tile.append(list, empty, more);
    let signature;
    function update(context) {
      const all = context.taskFeed?.tasks ?? [];
      const tasks = selectTasks(all, 6);
      const next = JSON.stringify(tasks.map(task => [task.id, task.title, task.status, task.done,
        task.total, task.status === 'completed' ? task.durationMs : task.elapsedMs])) + '|' + all.length;
      if (next === signature) return;
      signature = next;
      list.replaceChildren(...tasks.map(task => {
        const row = node('div', 'codex-row');
        row.dataset.status = task.status;
        row.dataset.source = task.source === 'antigravity' ? 'antigravity' : 'codex';
        row.append(node('span', 'codex-dot'));
        const name = node('span', 'codex-name');
        name.append(node('span', 'codex-source', task.source === 'antigravity' ? 'Anti' : 'Codex'));
        name.append(document.createTextNode(task.title || '任务'));
        row.append(name);
        const elapsed = codexTime(task.status === 'completed' ? task.durationMs : task.elapsedMs);
        row.append(node('span', 'codex-time', task.total > 0 ? task.done + '/' + task.total + ' · ' + elapsed : elapsed));
        row.append(node('span', 'codex-state', task.status === 'running' ? '执行中' : '已完成'));
        return row;
      }));
      empty.hidden = tasks.length > 0;
      more.textContent = all.length > tasks.length ? '还有 ' + (all.length - tasks.length) + ' 个任务' : '';
      more.hidden = all.length <= tasks.length;
    }
    return { element: tile, update };
  }

  function selectTasks(all, limit) {
    const sorted = all.slice().sort(compareTasks);
    const picked = [];
    const ids = new Set();
    for (const source of ['codex', 'antigravity']) {
      const group = sorted.filter(task => (task.source === 'antigravity' ? 'antigravity' : 'codex') === source);
      for (const task of group.slice(0, 3)) {
        picked.push(task);
        ids.add(task.id);
      }
    }
    for (const task of sorted) {
      if (picked.length >= limit) break;
      if (!ids.has(task.id)) {
        picked.push(task);
        ids.add(task.id);
      }
    }
    return picked.sort(compareTasks);
  }

  function compareTasks(left, right) {
    const order = (left.status === 'running' ? 0 : 1) - (right.status === 'running' ? 0 : 1);
    if (order) return order;
    return left.status === 'running'
      ? (right.startedAtMs || 0) - (left.startedAtMs || 0)
      : (right.completedAtMs || 0) - (left.completedAtMs || 0);
  }

  function codexTime(value) {
    if (!Number.isFinite(value) || value < 0) return '—';
    const seconds = Math.floor(value / 1000);
    const hours = Math.floor(seconds / 3600);
    const minutes = Math.floor(seconds % 3600 / 60);
    const rest = seconds % 60;
    return (hours ? hours + ':' + String(minutes).padStart(2, '0') : String(minutes)) +
      ':' + String(rest).padStart(2, '0');
  }

  function makeShortcut(widget, press) {
    const tile = node('button', 'widget widget-' + widget.size + ' widget-shortcut');
    tile.style.setProperty('--widget-accent', widget.color);
    tile.type = 'button';
    tile.disabled = !press;
    const name = widget.name.trim();
    const caption = name === '键盘快捷键' ? '' : name;
    tile.setAttribute('aria-label', [caption, widget.keys].filter(Boolean).join(' · ') || '快捷键');
    if (press) tile.addEventListener('click', () => press(widget, tile));
    const chord = node('span', 'widget-keycaps');
    chord.setAttribute('aria-hidden', 'true');
    const keys = (widget.keys ?? '').split('+').map(key => key.trim()).filter(Boolean);
    chord.append(...keys.flatMap((key, index) => {
      const cap = node('kbd', '', key);
      return index ? [node('span', 'widget-key-join', '+'), cap] : [cap];
    }));
    tile.append(chord);
    if (caption) tile.append(node('span', 'widget-label', caption));
    return { element: tile, update() {} };
  }

  function makeMusic(widget, control) {
    const tile = node('article', 'widget widget-' + widget.size + ' widget-music');
    tile.style.setProperty('--widget-accent', widget.color);
    const cover = node('div', 'widget-music-cover');
    const image = node('img');
    image.alt = '';
    image.hidden = true;
    image.addEventListener('load', () => { image.hidden = false; });
    image.addEventListener('error', () => { image.hidden = true; });
    cover.append(image);
    const title = node('span', 'widget-music-title');
    const bar = node('div', 'widget-music-progress');
    bar.setAttribute('role', 'progressbar');
    bar.setAttribute('aria-label', '播放进度');
    const fill = node('span');
    bar.append(fill);
    const transport = node('div', 'widget-music-controls');
    const labels = { previous: '上一首', toggle: '播放', next: '下一首' };
    const buttons = Object.entries(labels).map(([action, label]) => {
      const button = node('button', 'widget-music-button');
      button.type = 'button';
      button.dataset.action = action;
      button.setAttribute('aria-label', label);
      button.disabled = true;
      const icon = node('i', 'widget-music-icon');
      icon.setAttribute('aria-hidden', 'true');
      button.append(icon);
      if (control) button.addEventListener('click', async () => {
        pending = true;
        for (const item of buttons) item.disabled = true;
        try { await control(widget, action); }
        finally { pending = false; }
      });
      return button;
    });
    transport.append(...buttons);
    tile.append(cover, title, bar, transport);
    let url;
    let pending = false;

    function update(context) {
      const state = context.state;
      if (state?.cover !== url) {
        url = state?.cover;
        image.hidden = true;
        if (url) image.src = url;
        else image.removeAttribute('src');
      }
      replaceText(title, state?.song?.title || '未播放');
      const ratio = state?.durationMs > 0 && Number.isFinite(state.positionMs)
        ? Math.max(0, Math.min(1, state.positionMs / state.durationMs)) : 0;
      fill.style.setProperty('--music-progress', ratio);
      bar.setAttribute('aria-valuenow', String(Math.round(ratio * 100)));
      const capabilities = state?.playback ?? {};
      for (const [index, flag] of ['canPrevious', 'canToggle', 'canNext'].entries()) {
        buttons[index].disabled = !control || pending || !capabilities[flag];
      }
      buttons[1].dataset.playing = String(Boolean(state?.playing));
      buttons[1].setAttribute('aria-label', state?.playing ? '暂停' : '播放');
    }
    return { element: tile, update };
  }

  return { make, metrics, node };
})();
