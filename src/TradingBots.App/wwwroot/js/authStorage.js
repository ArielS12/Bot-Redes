window.tradingBotsAuth = {
  get: function () {
    try {
      return localStorage.getItem('tradingbots.auth');
    } catch {
      return null;
    }
  },
  set: function (value) {
    try {
      localStorage.setItem('tradingbots.auth', value);
    } catch {
    }
  },
  remove: function () {
    try {
      localStorage.removeItem('tradingbots.auth');
    } catch {
    }
  }
};
