// Small helpers for the sign-up form. The form works without them.
(function () {
  "use strict";
  var user = document.getElementById("username");
  var pw = document.getElementById("password");
  var confirm = document.getElementById("confirm");
  var userHelp = document.getElementById("username-help");
  var confirmHelp = document.getElementById("confirm-help");
  var userHelpText = userHelp ? userHelp.textContent : "";

  if (user && userHelp) {
    user.addEventListener("input", function () {
      var v = user.value;
      if (/[A-Z]/.test(v)) {
        userHelp.textContent = "Use lowercase letters only: capital letters do not work in the game.";
        userHelp.className = "help warn";
      } else if (v && /[^a-z0-9]/.test(v)) {
        userHelp.textContent = "Only lowercase letters a-z and numbers 0-9.";
        userHelp.className = "help warn";
      } else {
        userHelp.textContent = userHelpText;
        userHelp.className = "help";
      }
    });
  }

  function checkMatch() {
    if (!confirm || !confirmHelp) return;
    if (!confirm.value) { confirmHelp.textContent = ""; confirmHelp.className = "help"; return; }
    if (confirm.value === pw.value) {
      confirmHelp.textContent = "Passwords match.";
      confirmHelp.className = "help good";
    } else {
      confirmHelp.textContent = "The passwords are not the same yet.";
      confirmHelp.className = "help warn";
    }
  }
  if (pw && confirm) {
    pw.addEventListener("input", checkMatch);
    confirm.addEventListener("input", checkMatch);
  }

  var buttons = document.querySelectorAll("button.show");
  Array.prototype.forEach.call(buttons, function (b) {
    var target = document.getElementById(b.getAttribute("data-for"));
    if (!target) return;
    b.hidden = false;
    b.addEventListener("click", function () {
      var showing = target.type === "text";
      target.type = showing ? "password" : "text";
      if (confirm) confirm.type = target.type;
      b.textContent = showing ? "Show" : "Hide";
    });
  });
})();
