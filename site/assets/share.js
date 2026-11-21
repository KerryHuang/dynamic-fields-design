(function () {
  var URL_ = 'https://kerryhuang.github.io/formwright/';
  var TITLE = 'Formwright 表匠｜公司的系統，自己拼';
  var TEXT = 'Formwright 表匠：公司的系統，自己拼。\n像疊積木一樣拼出客戶管理、進銷存、生產管理，不寫程式、不等外包，流程變了打開來改就好。\n免註冊試玩 Demo 👉';
  var FULL = TEXT + ' ' + URL_;

  var dlg = document.getElementById('share-dialog');
  var status = document.getElementById('share-status');
  document.getElementById('share-preview').textContent = FULL;
  document.getElementById('share-line').href =
    'https://social-plugins.line.me/lineit/share?url=' + encodeURIComponent(URL_) + '&text=' + encodeURIComponent(TEXT);
  document.getElementById('share-fb').href =
    'https://www.facebook.com/sharer/sharer.php?u=' + encodeURIComponent(URL_);

  function openDialog() {
    status.textContent = '';
    if (dlg.showModal) dlg.showModal(); else dlg.setAttribute('open', '');
  }

  function share() {
    // 手機：叫出系統分享選單（LINE、Messenger、簡訊等都在裡面）；不支援才開自家面板
    if (navigator.share) {
      navigator.share({ title: TITLE, text: TEXT, url: URL_ }).catch(function (e) {
        if (e && e.name !== 'AbortError') openDialog();
      });
    } else {
      openDialog();
    }
  }

  document.querySelectorAll('[data-share]').forEach(function (b) { b.addEventListener('click', share); });

  document.getElementById('share-copy').addEventListener('click', function () {
    var done = function () { status.textContent = '已複製，貼到聊天室就好。'; };
    if (navigator.clipboard && window.isSecureContext) {
      navigator.clipboard.writeText(FULL).then(done, fallback);
    } else { fallback(); }
    function fallback() {
      var ta = document.createElement('textarea');
      ta.value = FULL; ta.setAttribute('readonly', ''); ta.style.position = 'fixed'; ta.style.opacity = '0';
      document.body.appendChild(ta); ta.select();
      try { document.execCommand('copy'); done(); } catch (e) { status.textContent = '複製失敗，請手動選取上面的文字。'; }
      document.body.removeChild(ta);
    }
  });

  document.getElementById('share-close').addEventListener('click', function () {
    if (dlg.close) dlg.close(); else dlg.removeAttribute('open');
  });
  dlg.addEventListener('click', function (e) { if (e.target === dlg && dlg.close) dlg.close(); });
})();
