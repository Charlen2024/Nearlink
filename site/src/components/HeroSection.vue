<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue'

const assetBase = import.meta.env.BASE_URL
const slides = [
  {
    image: `${assetBase}screenshots/desktop-devices.png`,
    title: '设备连接',
    description: 'Windows · 在线设备',
    alt: 'Nearlink Windows 端的在线设备连接界面',
    layout: 'desktop',
  },
  {
    image: `${assetBase}screenshots/android-settings.png`,
    title: '移动端设置',
    description: 'Android · 主题与设备管理',
    alt: 'Nearlink Android 端的主题与设备设置界面',
    layout: 'mobile',
  },
] as const

const track = ref<HTMLElement | null>(null)
const currentIndex = ref(window.matchMedia('(max-width: 720px)').matches ? 1 : 0)
const isPlaying = ref(false)
let timer: number | undefined
let mobileQuery: MediaQueryList | undefined

function goToSlide(index: number) {
  const next = (index + slides.length) % slides.length
  currentIndex.value = next
  const viewport = track.value
  if (viewport) viewport.scrollTo({ left: next * viewport.clientWidth, behavior: 'smooth' })
}

function syncCurrentSlide() {
  if (!track.value) return
  currentIndex.value = Math.round(track.value.scrollLeft / track.value.clientWidth)
}

function startRotation() {
  if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) return
  window.clearInterval(timer)
  isPlaying.value = true
  timer = window.setInterval(() => goToSlide(currentIndex.value + 1), 5600)
}

function stopRotation() {
  window.clearInterval(timer)
  isPlaying.value = false
}

function toggleRotation() {
  if (isPlaying.value) stopRotation()
  else startRotation()
}

function syncViewportMode(event?: MediaQueryListEvent) {
  const nextMode = event?.matches ?? window.matchMedia('(max-width: 720px)').matches
  const nextIndex = nextMode ? 1 : 0
  if (currentIndex.value === nextIndex) return
  currentIndex.value = nextIndex
  requestAnimationFrame(() => {
    if (track.value) track.value.scrollLeft = nextIndex * track.value.clientWidth
  })
}

onMounted(() => {
  mobileQuery = window.matchMedia('(max-width: 720px)')
  syncViewportMode()
  if (track.value) track.value.scrollLeft = currentIndex.value * track.value.clientWidth
  mobileQuery.addEventListener('change', syncViewportMode)
  startRotation()
})
onUnmounted(() => {
  stopRotation()
  mobileQuery?.removeEventListener('change', syncViewportMode)
})
</script>

<template>
  <section class="hero" aria-labelledby="hero-title">
    <div class="hero-showcase">
      <div ref="track" class="showcase-track" role="region" aria-roledescription="carousel" aria-label="Nearlink 实际界面展示" @scroll.passive="syncCurrentSlide">
        <figure v-for="(slide, index) in slides" :key="slide.title" class="showcase-slide" :aria-label="`第 ${index + 1} 张，共 ${slides.length} 张`">
          <div class="screenshot-stage" :class="`screenshot-${slide.layout}`">
            <div v-if="slide.layout !== 'mobile'" class="window-chrome" aria-hidden="true"><span></span><span></span><span></span><small>Nearlink</small></div>
            <div class="screenshot-content">
              <img :src="slide.image" :alt="slide.alt" />
            </div>
          </div>
          <figcaption><strong>{{ slide.title }}</strong><span>{{ slide.description }}</span></figcaption>
        </figure>
      </div>
      <div class="showcase-controls" aria-label="界面图片轮播控制">
        <button type="button" class="showcase-arrow" aria-label="上一张界面" @click="goToSlide(currentIndex - 1)"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M19 12H5m0 0 7-7m-7 7 7 7" /></svg></button>
        <div class="showcase-control-center">
          <div class="showcase-dots">
            <button v-for="(slide, index) in slides" :key="slide.title" type="button" :class="{ active: index === currentIndex }" :aria-label="`显示${slide.title}`" :aria-current="index === currentIndex ? 'true' : undefined" @click="goToSlide(index)"></button>
          </div>
          <button type="button" class="showcase-play" :aria-label="isPlaying ? '暂停自动播放' : '开始自动播放'" @click="toggleRotation">
            <svg v-if="isPlaying" viewBox="0 0 24 24" aria-hidden="true"><path d="M7 5h3.5v14H7zM13.5 5H17v14h-3.5z" /></svg>
            <svg v-else viewBox="0 0 24 24" aria-hidden="true"><path d="M8 5.5v13l10-6.5z" /></svg>
          </button>
        </div>
        <button type="button" class="showcase-arrow" aria-label="下一张界面" @click="goToSlide(currentIndex + 1)"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M5 12h14m0 0-7-7m7 7-7 7" /></svg></button>
      </div>
    </div>
    <div class="hero-copy">
      <p class="eyebrow">NEARLINK · WINDOWS & ANDROID</p>
      <h1 id="hero-title">让设备之间，<br />靠近一点。</h1>
      <p class="hero-description">手机、平板和电脑，在同一个局域网里直接发送消息与文件。无需云盘中转，打开 Nearlink 就能开始。</p>
      <div class="hero-actions">
        <a class="button button-primary" href="#downloads">了解如何获取</a>
        <a class="plain-link" href="https://github.com/Charlen2024/Nearlink" target="_blank" rel="noreferrer">探索开源项目</a>
      </div>
      <p class="hero-note">手机 ↔ 电脑　·　电脑 ↔ 电脑　·　手机 ↔ 手机</p>
    </div>
  </section>
</template>
