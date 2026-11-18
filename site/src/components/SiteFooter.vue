<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue'

const showBackToTop = ref(false)
const footerGroups = [
  {
    title: 'Nearlink',
    links: [
      { label: '功能介绍', href: '#features' },
      { label: '传输方式', href: '#how-it-works' },
      { label: '开始使用', href: '#downloads' },
    ],
  },
  {
    title: '获取应用',
    links: [
      { label: 'Windows 构建说明', href: 'https://github.com/Charlen2024/Nearlink#build' },
      { label: 'Android 构建说明', href: 'https://github.com/Charlen2024/Nearlink#android' },
      { label: '版本与更新', href: 'https://github.com/Charlen2024/Nearlink/releases' },
    ],
  },
  {
    title: '开源项目',
    links: [
      { label: 'GitHub 仓库', href: 'https://github.com/Charlen2024/Nearlink' },
      { label: '反馈问题', href: 'https://github.com/Charlen2024/Nearlink/issues' },
      { label: '项目主页', href: 'https://github.com/Charlen2024' },
    ],
  },
  {
    title: '关于',
    links: [
      { label: 'Designed by Chao', href: 'https://github.com/charlen2024' },
      { label: '隐私与安全', href: '#privacy' },
      { label: '本地传输说明', href: '#how-it-works' },
    ],
  },
]

function updateScrollState() {
  const footer = document.querySelector('.site-footer')
  const footerVisible = footer ? footer.getBoundingClientRect().top < window.innerHeight : false
  showBackToTop.value = window.scrollY > 500 && !footerVisible
}

function scrollToTop() {
  window.scrollTo({
    top: 0,
    behavior: window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'instant' : 'smooth',
  })
}

onMounted(() => {
  updateScrollState()
  window.addEventListener('scroll', updateScrollState, { passive: true })
})

onUnmounted(() => window.removeEventListener('scroll', updateScrollState))
</script>

<template>
  <footer class="site-footer">
    <div class="footer-follow">
      <div class="follow-inner">
        <span>关注 Nearlink</span>
        <a class="social-link" href="https://github.com/Charlen2024/Nearlink" aria-label="在 GitHub 关注 Nearlink 项目" target="_blank" rel="noreferrer">
          <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 .9a11.1 11.1 0 0 0-3.51 21.63c.55.1.76-.24.76-.54v-2.08c-3.1.67-3.76-1.32-3.76-1.32-.5-1.29-1.24-1.63-1.24-1.63-1.01-.69.08-.68.08-.68 1.12.08 1.71 1.15 1.71 1.15.99 1.7 2.6 1.21 3.23.93.1-.72.39-1.21.71-1.49-2.47-.28-5.07-1.24-5.07-5.5 0-1.22.44-2.2 1.15-2.97-.12-.28-.5-1.42.11-2.95 0 0 .94-.3 3.06 1.14a10.6 10.6 0 0 1 5.57 0c2.12-1.44 3.06-1.14 3.06-1.14.61 1.53.23 2.67.11 2.95.72.77 1.15 1.75 1.15 2.97 0 4.27-2.6 5.22-5.08 5.49.4.35.76 1.02.76 2.06v3.07c0 .3.2.65.77.54A11.1 11.1 0 0 0 12 .9Z"/></svg>
        </a>
      </div>
    </div>
    <div class="footer-directory">
      <section v-for="group in footerGroups" :key="group.title" class="footer-group">
        <h2>{{ group.title }}</h2>
        <ul>
          <li v-for="link in group.links" :key="link.label">
            <a :href="link.href" :target="link.href.startsWith('https://') ? '_blank' : undefined" :rel="link.href.startsWith('https://') ? 'noreferrer' : undefined">{{ link.label }}</a>
          </li>
        </ul>
      </section>
    </div>
    <div class="footer-legal">
      <div class="footer-locale"><span aria-hidden="true">◎</span> 中文（中国）</div>
      <nav aria-label="法律与版权">
        <a href="#privacy">隐私与安全</a>
        <a href="https://github.com/Charlen2024/Nearlink/blob/main/LICENSE" target="_blank" rel="noreferrer">开源许可</a>
        <span>© 2026 Nearlink · Designed by Chao</span>
      </nav>
    </div>
    <Transition name="back-top">
      <button v-if="showBackToTop" class="back-to-top" type="button" aria-label="返回页首" @click="scrollToTop">返回页首</button>
    </Transition>
  </footer>
</template>
