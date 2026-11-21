// 把 1280x720 的舞台縮放到視窗大小（錄影時視窗設 1280x720 即 1:1）
(function () {
  var fit = document.querySelector('.stage-fit');
  var stage = fit.firstElementChild;
  function resize() {
    var s = Math.min(window.innerWidth / 1280, window.innerHeight / 720);
    stage.style.transformOrigin = '0 0';
    stage.style.transform = 'scale(' + s + ')';
    fit.style.width = 1280 * s + 'px';
    fit.style.height = 720 * s + 'px';
  }
  window.addEventListener('resize', resize);
  resize();
})();
