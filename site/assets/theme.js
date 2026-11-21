(function () {
  var KEY = 'fw-theme';
  var root = document.documentElement;
  var buttons = document.querySelectorAll('[data-theme-set]');

  function read() {
    try { var v = localStorage.getItem(KEY); return v === 'light' || v === 'dark' ? v : 'system'; }
    catch (e) { return 'system'; }
  }

  function apply(mode) {
    if (mode === 'system') delete root.dataset.theme; else root.dataset.theme = mode;
    buttons.forEach(function (b) { b.setAttribute('aria-pressed', String(b.dataset.themeSet === mode)); });
  }

  buttons.forEach(function (b) {
    b.addEventListener('click', function () {
      var mode = b.dataset.themeSet;
      try { if (mode === 'system') localStorage.removeItem(KEY); else localStorage.setItem(KEY, mode); } catch (e) {}
      apply(mode);
    });
  });

  apply(read());
})();
