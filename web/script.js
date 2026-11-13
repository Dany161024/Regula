// Regula — web: tlačidlá „Copy" pri príkazoch a video bez pohybu pre tých, čo si ho vypli.

document.querySelectorAll("pre.code").forEach((pre) => {
  const button = document.createElement("button");
  button.className = "copy";
  button.type = "button";
  button.textContent = "Copy";
  button.addEventListener("click", async () => {
    try {
      await navigator.clipboard.writeText(pre.querySelector("code").innerText);
      button.textContent = "Copied";
    } catch {
      button.textContent = "Select & copy";
    }
    setTimeout(() => (button.textContent = "Copy"), 1600);
  });
  const box = document.createElement("div");     // tlačidlo mimo <pre>, aby sa neposúvalo s dlhým riadkom
  box.className = "codebox";
  pre.before(box);
  box.append(pre, button);
});

const video = document.querySelector(".screen video");
if (video && window.matchMedia("(prefers-reduced-motion: reduce)").matches) {
  video.removeAttribute("autoplay");
  video.pause();
  video.controls = true;
}
