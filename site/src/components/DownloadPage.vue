<script setup lang="ts">
import { computed } from 'vue'

const props = defineProps<{ platform: 'windows' | 'android' }>()

const details = computed(() => props.platform === 'windows'
  ? {
      name: 'Nearlink for Windows',
      label: 'Windows 桌面端',
      summary: '在 Windows 电脑上接收附近设备，发送消息与文件，并从资源管理器快速互传。',
      requirement: 'Windows 10 及以上 · x64',
      packageName: 'Nearlink.Windows.zip',
      guide: 'https://github.com/Charlen2024/Nearlink#build',
      guideLabel: '查看 Windows 构建说明',
      icon: '▦',
    }
  : {
      name: 'Nearlink for Android',
      label: 'Android 手机与平板',
      summary: '发现同一局域网中的手机和电脑，直接发送照片、文件与消息。支持手机和平板布局。',
      requirement: 'Android 8.0 及以上 · ARM64',
      packageName: 'Nearlink-Android.apk',
      guide: 'https://github.com/Charlen2024/Nearlink#android',
      guideLabel: '查看 Android 构建说明',
      icon: '◉',
    })
</script>

<template>
  <section class="client-download section">
    <a class="download-back" href="/nearlink/#downloads">返回客户端选择</a>
    <div class="client-download-heading">
      <span class="client-download-icon" aria-hidden="true">{{ details.icon }}</span>
      <p class="eyebrow">{{ details.label }}</p>
      <h1>{{ details.name }}</h1>
      <p class="client-download-summary">{{ details.summary }}</p>
    </div>

    <div class="release-panel">
      <div class="release-copy">
        <span class="release-status"><i></i> 尚未发布安装包</span>
        <h2>下载包准备中</h2>
        <p>当前公开仓库还没有上传可直接安装的 {{ details.packageName }}。发布后，这里会提供安装包和版本说明。</p>
        <div class="release-meta"><span>系统要求</span><strong>{{ details.requirement }}</strong></div>
      </div>
      <div class="release-actions">
        <a class="button button-primary release-button" :href="details.guide" target="_blank" rel="noreferrer">{{ details.guideLabel }}</a>
        <a class="release-history" href="https://github.com/Charlen2024/Nearlink/releases" target="_blank" rel="noreferrer">查看 GitHub Release 页面</a>
      </div>
    </div>

    <div class="download-help">
      <h2>如何获取客户端</h2>
      <p>目前可以按构建说明从源码生成安装包。正式安装包发布后，无需构建即可从本页直接下载。</p>
      <a href="https://github.com/Charlen2024/Nearlink" target="_blank" rel="noreferrer">浏览 Nearlink 开源仓库</a>
    </div>
  </section>
</template>
