using System;

internal static class DreamThemeRuntime
{
    private const string GundamId = "custom-1784518930245";
    private const string BocchiId = "custom-bocchi-gotoh";
    private const string GundamCatalogId = "gandum";
    private const string BocchiCatalogId = "theme-1786780354717";

    internal static bool Supports(string themeId)
    {
        return String.Equals(themeId, GundamId, StringComparison.Ordinal) ||
            String.Equals(themeId, BocchiId, StringComparison.Ordinal) ||
            String.Equals(themeId, GundamCatalogId, StringComparison.Ordinal) ||
            String.Equals(themeId, BocchiCatalogId, StringComparison.Ordinal);
    }

    internal static string BuildInstallScript(string themeId)
    {
        if (!Supports(themeId)) return "";
        bool gundam = String.Equals(themeId, GundamId, StringComparison.Ordinal) ||
            String.Equals(themeId, GundamCatalogId, StringComparison.Ordinal);
        string variant = gundam ? "rx78-white-base" : "bocchi-gotoh";
        string shell = gundam ? "light" : "dark";
        return @"
  const dreamState = { saved: {}, marked: [], classes: [], decor: [], listeners: [], active: [], home: false };
  const dreamRootAttributes = ['data-midweb-dream-theme', 'data-dream-shell', 'data-dream-theme-variant', 'data-ds-route',
    'data-dream-art-wide', 'data-dream-art-safe', 'data-dream-art-safe-area', 'data-dream-task-mode',
    'data-dream-art-task-mode', 'data-dream-art-aspect', 'data-dream-art-ready'];
  dreamRootAttributes.forEach((name) => {
    dreamState.saved[name] = { present: root.hasAttribute(name), value: root.getAttribute(name) };
  });
  const dreamClearMarks = () => {
    dreamState.marked.forEach((entry) => {
      const node = entry.node;
      if (!node) return;
      if (node.getAttribute('data-midweb-dream-part') === 'owned') {
        if (node.getAttribute('data-ds-part') === entry.value) node.removeAttribute('data-ds-part');
        node.removeAttribute('data-midweb-dream-part');
      }
    });
    dreamState.marked = [];
  };
  const dreamMarkPart = (node, value) => {
    if (!node) return;
    const current = node.getAttribute('data-ds-part');
    const owner = node.getAttribute('data-midweb-dream-part');
    if (node.hasAttribute('data-ds-part') && owner !== 'owned') return;
    node.setAttribute('data-ds-part', value);
    node.setAttribute('data-midweb-dream-part', 'owned');
    dreamState.marked.push({ node: node, value: value });
  };
  const dreamClearClasses = () => {
    dreamState.classes.forEach((entry) => {
      const node = entry.node;
      if (node && node.getAttribute('data-midweb-dream-class') &&
          node.getAttribute('data-midweb-dream-class').split(',').indexOf(entry.value) >= 0) {
        node.classList.remove(entry.value);
        const rest = node.getAttribute('data-midweb-dream-class').split(',').filter((name) => name !== entry.value);
        if (rest.length) node.setAttribute('data-midweb-dream-class', rest.join(',')); else node.removeAttribute('data-midweb-dream-class');
      }
    });
    dreamState.classes = [];
  };
  const dreamSetClass = (node, value, enabled) => {
    if (!node) return;
    const owner = node.getAttribute('data-midweb-dream-class') || '';
    if (enabled) {
      if (node.classList.contains(value) && owner.split(',').indexOf(value) < 0) return;
      if (!node.classList.contains(value)) node.classList.add(value);
      if (owner.split(',').indexOf(value) < 0)
        node.setAttribute('data-midweb-dream-class', owner ? owner + ',' + value : value);
      dreamState.classes.push({ node: node, value: value });
    } else if (owner.split(',').indexOf(value) >= 0) {
      node.classList.remove(value);
      const rest = owner.split(',').filter((name) => name !== value);
      if (rest.length) node.setAttribute('data-midweb-dream-class', rest.join(',')); else node.removeAttribute('data-midweb-dream-class');
    }
  };
  const dreamElement = (tag, className, text) => {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text) node.textContent = text;
    return node;
  };
  const dreamCreateDecor = (main) => {
    let chrome = document.getElementById('codex-dream-skin-chrome');
    let chromeCreated = false;
    if (!chrome) {
      chrome = dreamElement('div', '', '');
      chrome.id = 'codex-dream-skin-chrome';
      chrome.setAttribute('aria-hidden', 'true');
      chrome.setAttribute('data-midweb-dream-owned', 'owned');
      const brandNode = dreamElement('div', 'dream-skin-brand', '');
      brandNode.appendChild(dreamElement('span', 'dream-skin-portal-mark', '◉'));
      const brandText = dreamElement('span', '', '');
      brandText.appendChild(dreamElement('b', '', '" + (gundam ? "RX-78-2 · WHITE BASE" : "Bocchi the Rock! · 波奇酱") + @"'));
      brandText.appendChild(dreamElement('small', '', 'CODEX DREAM SKIN'));
      brandNode.appendChild(brandText);
      chrome.appendChild(brandNode);
      const statusNode = dreamElement('div', 'dream-skin-status', '');
      statusNode.appendChild(dreamElement('i', '', ''));
      statusNode.appendChild(dreamElement('span', '', '" + (gundam ? "DREAM SKIN ONLINE" : "") + @"'));
      chrome.appendChild(statusNode);
      chrome.appendChild(dreamElement('div', 'dream-skin-quote', '" + (gundam ? "RX-78-2 / WHITE BASE" : "ひとりぼっちじゃない") + @"'));
      const particles = dreamElement('div', 'dream-skin-particles', '');
      for (let index = 0; index < 8; index += 1) particles.appendChild(dreamElement('i', '', ''));
      chrome.appendChild(particles);
      chrome.appendChild(dreamElement('div', 'dream-skin-orbit', ''));
      document.body.appendChild(chrome);
      dreamState.decor.push(chrome);
      chromeCreated = true;
    }
    if (!chromeCreated && chrome.getAttribute('data-midweb-dream-owned') !== 'owned') return;
    dreamState.chrome = chrome;
    chrome.style.display = main && main.getAttribute('data-codexthemes-page') !== 'system' ? '' : 'none';
    if (!" + (gundam ? "true" : "false") + @") return;
    let polaroid = document.getElementById('ds-polaroid-draggable');
    let polaroidCreated = false;
    if (!polaroid) {
      polaroid = dreamElement('div', '', '');
      polaroid.id = 'ds-polaroid-draggable';
      polaroid.setAttribute('aria-hidden', 'true');
      polaroid.setAttribute('data-midweb-dream-owned', 'owned');
      polaroid.appendChild(dreamElement('div', 'ds-pola-photo', ''));
      polaroid.appendChild(dreamElement('div', 'ds-pola-cap', 'SCV-70 WHITE BASE'));
      polaroid.appendChild(dreamElement('div', 'ds-pola-handle', ''));
      document.body.appendChild(polaroid);
      dreamState.decor.push(polaroid);
      polaroidCreated = true;
    }
    if (!polaroidCreated && polaroid.getAttribute('data-midweb-dream-owned') !== 'owned') return;
    dreamState.polaroid = polaroid;
    polaroid.style.display = dreamState.home ? '' : 'none';
  };
  const dreamLayoutDecor = (main) => {
    const chrome = dreamState.chrome;
    if (!chrome || chrome.getAttribute('data-midweb-dream-owned') !== 'owned') return;
    const shellMain = main || document.querySelector('main.main-surface') || document.querySelector('main');
    if (shellMain && main && main.getAttribute('data-codexthemes-page') !== 'system') {
      const box = shellMain.getBoundingClientRect();
      chrome.style.left = Math.round(box.left) + 'px';
      chrome.style.top = Math.round(box.top) + 'px';
      chrome.style.width = Math.round(box.width) + 'px';
      chrome.style.height = Math.round(box.height) + 'px';
    }
    dreamSetClass(chrome, 'dream-skin-home-shell', Boolean(main && main.getAttribute('data-codexthemes-page') === 'home'));
  };
  const dreamBindPolaroid = (el) => {
    if (!el || dreamState.polaroidBound === el) return;
    dreamState.polaroidBound = el;
    const handle = el.querySelector('.ds-pola-handle');
    let mode = null, startX = 0, startY = 0, startLeft = 0, startTop = 0, startScale = 1, startRotation = 7, startDistance = 1, startAngle = 0;
    const currentTransform = () => {
      const match = /rotate\(([-\d.]+)deg\)\s*scale\(([-\d.]+)\)/.exec(el.style.transform || '');
      return { rotation: match ? parseFloat(match[1]) : 7, scale: match ? parseFloat(match[2]) : 1 };
    };
    const center = () => {
      const box = el.getBoundingClientRect();
      return { x: box.left + box.width / 2, y: box.top + box.height / 2 };
    };
    const removeActive = () => {
      dreamState.active.forEach((entry) => window.removeEventListener(entry[0], entry[1], true));
      dreamState.active = [];
      mode = null;
    };
    const move = (event) => {
      if (!mode) return;
      if (mode === 'move') {
        el.style.left = startLeft + event.clientX - startX + 'px';
        el.style.top = startTop + event.clientY - startY + 'px';
      } else {
        const point = center();
        const distance = Math.max(1, Math.hypot(event.clientX - point.x, event.clientY - point.y));
        const angle = Math.atan2(event.clientY - point.y, event.clientX - point.x);
        const scale = Math.min(3, Math.max(0.4, startScale * distance / startDistance));
        const rotation = startRotation + (angle - startAngle) * 180 / Math.PI;
        el.style.transform = 'rotate(' + rotation + 'deg) scale(' + scale + ')';
      }
    };
    const up = () => removeActive();
    const down = (event, nextMode) => {
      event.preventDefault();
      event.stopPropagation();
      mode = nextMode;
      startX = event.clientX;
      startY = event.clientY;
      startLeft = parseFloat(el.style.left) || 0;
      startTop = parseFloat(el.style.top) || 0;
      const transform = currentTransform();
      startScale = transform.scale;
      startRotation = transform.rotation;
      const point = center();
      startDistance = Math.max(1, Math.hypot(event.clientX - point.x, event.clientY - point.y));
      startAngle = Math.atan2(event.clientY - point.y, event.clientX - point.x);
      dreamState.active = [['pointermove', move], ['pointerup', up]];
      dreamState.active.forEach((entry) => window.addEventListener(entry[0], entry[1], true));
    };
    const reset = (event) => {
      event.preventDefault();
      el.style.left = Math.max(12, window.innerWidth - 150) + 'px';
      el.style.top = Math.max(12, window.innerHeight - 320) + 'px';
      el.style.transform = 'rotate(7deg) scale(1)';
    };
    const downMove = (event) => {
      if (handle && handle.contains(event.target)) return;
      down(event, 'move');
    };
    const downScale = (event) => down(event, 'scale');
    el.style.left = Math.max(12, window.innerWidth - 150) + 'px';
    el.style.top = Math.max(12, window.innerHeight - 320) + 'px';
    el.style.transform = 'rotate(7deg) scale(1)';
    dreamState.listeners = [[el, 'pointerdown', downMove], [el, 'dblclick', reset]];
    dreamState.listeners.push([handle, 'pointerdown', downScale]);
    dreamState.listeners.forEach((entry) => { if (entry[0]) entry[0].addEventListener(entry[1], entry[2], true); });
  };
  dreamState.resizeHandler = () => dreamLayoutDecor(document.querySelector('main.main-surface') || document.querySelector('main'));
  window.addEventListener('resize', dreamState.resizeHandler, { passive: true });
  state.dreamClassify = (main) => {
    dreamClearMarks();
    dreamClearClasses();
    const isHome = Boolean(main && main.getAttribute('data-codexthemes-page') === 'home');
    dreamState.home = isHome;
    root.setAttribute('data-midweb-dream-theme', '" + themeId + @"');
    root.setAttribute('data-dream-shell', '" + shell + @"');
    root.setAttribute('data-dream-theme-variant', '" + variant + @"');
    const page = main ? main.getAttribute('data-codexthemes-page') : '';
    const isApp = Boolean(main && page !== 'system');
    root.setAttribute('data-ds-route', isApp ? 'app' : 'settings');
    root.setAttribute('data-dream-art-wide', isApp ? 'true' : 'false');
    root.setAttribute('data-dream-art-safe', 'left');
    root.setAttribute('data-dream-art-safe-area', 'left');
    root.setAttribute('data-dream-task-mode', 'ambient');
    root.setAttribute('data-dream-art-task-mode', 'ambient');
    root.setAttribute('data-dream-art-aspect', 'wide');
    root.setAttribute('data-dream-art-ready', 'true');
    dreamMarkPart(root, 'root');
    const sidebar = document.querySelector('aside.app-shell-left-panel');
    const header = main && (main.querySelector('header.app-header-tint') || main.querySelector('header'));
    const composer = document.querySelector('.composer-surface-chrome');
    const toolbar = composer && composer.querySelector('[role=""toolbar""], [data-composer-toolbar]');
    const homeHero = isHome && main.querySelector('[role=""main""]');
    const projectList = document.querySelector('[data-feature=""game-source""]');
    const message = main && main.querySelector('article');
    const thread = main && (main.querySelector('.thread-scroll-container') || main.querySelector('[data-thread-user-message-navigation-item-id]'));
    dreamMarkPart(header, 'header');
    dreamMarkPart(sidebar, 'sidebar');
    dreamMarkPart(homeHero, 'home-hero');
    dreamMarkPart(composer, 'composer');
    dreamMarkPart(toolbar, 'composer-toolbar');
    dreamMarkPart(projectList, 'project-list');
    dreamMarkPart(message, 'message');
    dreamMarkPart(thread, 'thread');
    document.querySelectorAll('[role=""dialog""]').forEach((node) => dreamMarkPart(node, 'dialog'));
    dreamSetClass(main, 'dream-skin-home-shell', isHome);
    dreamSetClass(homeHero, 'dream-skin-home', isHome);
    dreamCreateDecor(main);
    dreamLayoutDecor(main);
    if (dreamState.polaroid) dreamBindPolaroid(dreamState.polaroid);
  };
  state.dreamCleanup = () => {
    dreamState.active.forEach((entry) => window.removeEventListener(entry[0], entry[1], true));
    dreamState.active = [];
    if (dreamState.resizeHandler) window.removeEventListener('resize', dreamState.resizeHandler);
    dreamState.listeners.forEach((entry) => { if (entry[0]) entry[0].removeEventListener(entry[1], entry[2], true); });
    dreamState.listeners = [];
    dreamClearMarks();
    dreamClearClasses();
    dreamState.decor.forEach((node) => {
      if (node && node.getAttribute('data-midweb-dream-owned') === 'owned') node.remove();
    });
  dreamState.decor = [];
    dreamState.polaroidBound = null;
    dreamRootAttributes.forEach((name) => {
      const saved = dreamState.saved[name];
      if (saved && saved.present) root.setAttribute(name, saved.value); else root.removeAttribute(name);
    });
    if (root.getAttribute('data-midweb-dream-root-class') === 'owned') {
      root.classList.remove('codex-dream-skin');
      root.removeAttribute('data-midweb-dream-root-class');
    }
    state.dreamClassify = null;
    state.dreamCleanup = null;
  };
  if (!root.classList.contains('codex-dream-skin')) {
    root.classList.add('codex-dream-skin');
    root.setAttribute('data-midweb-dream-root-class', 'owned');
  }
";
    }

    internal static string BuildClassifyHook()
    {
        return "    if (state.dreamClassify) state.dreamClassify(main);\n";
    }

    internal static string BuildCleanupCall()
    {
        return "  if (state && state.dreamCleanup) state.dreamCleanup();\n";
    }
}
