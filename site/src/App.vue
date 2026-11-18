<script setup lang="ts">
import { ref } from 'vue'
import SiteHeader from './components/SiteHeader.vue'
import HeroSection from './components/HeroSection.vue'
import FeatureSection from './components/FeatureSection.vue'
import TransferSection from './components/TransferSection.vue'
import DownloadSection from './components/DownloadSection.vue'
import SiteFooter from './components/SiteFooter.vue'
import GetPage from './components/GetPage.vue'

const pathParts = window.location.pathname.split('/').filter(Boolean)
const isGetPage = pathParts[1] === 'get' || pathParts[1] === 'download'
const showAnnouncement = ref(true)

const features = [
  {
    number: '01',
    title: '设备互相发现',
    description: '连接同一局域网后查看附近设备。为手机和电脑设置自己的名称，一眼认出发送方与接收方。',
    symbol: '⌕',
  },
  {
    number: '02',
    title: '消息和文件，各自清楚',
    description: '短消息保留在会话里；图片显示缩略图，其他文件显示类型与大小，收到后可以多选和批量保存。',
    symbol: '▤',
  },
  {
    number: '03',
    title: '传输有校验，也能续上',
    description: 'Windows 文件上传使用分块传输、确认偏移与 SHA-256 校验；连接中断后可从已确认位置继续。',
    symbol: '↻',
  },
]

</script>

<template>
  <a class="skip-link" href="#main">跳到主要内容</a>
  <Transition name="announcement">
    <aside v-if="showAnnouncement" class="announcement">
      <p>手机、平板和电脑，连在同一局域网就能直接互传。<a href="/nearlink/#how-it-works">了解 Nearlink &gt;&gt;</a></p>
      <button type="button" aria-label="关闭公告" @click="showAnnouncement = false">
        <svg viewBox="0 0 24 24" aria-hidden="true"><path d="m6 6 12 12M18 6 6 18" /></svg>
      </button>
    </aside>
  </Transition>
  <SiteHeader />
  <main v-if="isGetPage" id="main">
    <GetPage />
  </main>
  <main v-else id="main">
    <HeroSection />
    <div class="capability-bar" aria-label="Nearlink 特点">
      <div v-for="(item, index) in ['多设备同时连接', '消息与文件分开展示', '图片即时预览', 'Windows 与 Android']" :key="item">
        <span class="capability-mark">{{ String(index + 1).padStart(2, '0') }}</span><span>{{ item }}</span>
      </div>
    </div>
    <FeatureSection :features="features" />
    <TransferSection />
    <DownloadSection />
  </main>
  <SiteFooter />
</template>
