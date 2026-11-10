(() => {
  for (const button of document.querySelectorAll('[data-copy]')) {
    button.addEventListener('click', async () => {
      const code = document.getElementById(button.dataset.copy);
      const status = button.closest('.usage-example').querySelector('.copy-status');
      status.textContent = 'Copying…';
      try {
        await Promise.race([
          navigator.clipboard.writeText(code.textContent),
          new Promise((_, reject) => setTimeout(() => reject(new Error('Clipboard unavailable')), 1500))
        ]);
        status.textContent = 'Code copied.';
      } catch {
        const selection = window.getSelection();
        const range = document.createRange();
        range.selectNodeContents(code);
        selection.removeAllRanges();
        selection.addRange(range);
        status.textContent = 'Code selected. Use your device’s Copy command.';
      }
    });
  }
})();
