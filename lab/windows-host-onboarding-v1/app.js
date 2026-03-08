const tabs = Array.from(document.querySelectorAll('.tab-btn'));
const panels = Array.from(document.querySelectorAll('.tab-panel'));

for (const tab of tabs) {
  tab.addEventListener('click', () => {
    const target = tab.dataset.tab;

    for (const button of tabs) {
      button.classList.toggle('active', button === tab);
    }

    for (const panel of panels) {
      panel.classList.toggle('active', panel.dataset.panel === target);
    }
  });
}