<script setup lang="ts">
const clients = [
  {
    platform: 'Windows',
    detail: 'Windows 10 及以上 · x64 预览版',
    package: 'Nearlink-Windows-x64.zip',
    href: '/nearlink/downloads/Nearlink-Windows-x64.zip',
    description: '适用于 Windows 桌面电脑。下载后解压并运行 Nearlink.exe。',
  },
  {
    platform: 'Android',
    detail: 'Android 8.0 及以上 · APK 预览版',
    package: 'Nearlink-Android.apk',
    href: '/nearlink/downloads/Nearlink-Android.apk',
    description: '适用于 Android 手机和平板。下载 APK 后打开文件即可安装。',
  },
]
</script>

<template>
  <section class="get-page section">
    <a class="download-back" href="/nearlink/">返回 Nearlink 首页</a>
    <div class="get-page-heading">
      <p class="eyebrow">获取 Nearlink</p>
      <h1>选择你的设备。</h1>
      <p>下载对应客户端，在同一局域网内开始互传消息与文件。</p>
    </div>

    <div class="client-download-options">
      <article v-for="client in clients" :key="client.platform" class="client-download-card">
        <span class="client-card-icon" :class="`client-card-icon-${client.platform.toLowerCase()}`" aria-hidden="true">
          <svg v-if="client.platform === 'Windows'" viewBox="0 0 24 24" fill="currentColor">
            <path d="M2 4.6 10.7 3.4v8.1H2V4.6Zm10.2-1.4L22 1.8v9.7h-9.8V3.2ZM2 12.8h8.7v8.1L2 19.7v-6.9Zm10.2 0H22v9.7l-9.8-1.4v-8.3Z" />
          </svg>
          <svg v-else viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round">
            <path d="m7.1 5.2-1.5-2M16.9 5.2l1.5-2M5 10.2a7 7 0 0 1 14 0v.7H5v-.7Z" />
            <path d="M5 12.5h14v5.2a1 1 0 0 1-1 1H6a1 1 0 0 1-1-1v-5.2ZM5 13l-1.5 4M19 13l1.5 4M8.5 18.7v2M15.5 18.7v2" />
            <circle cx="8.5" cy="8.3" r=".55" fill="currentColor" stroke="none" />
            <circle cx="15.5" cy="8.3" r=".55" fill="currentColor" stroke="none" />
          </svg>
        </span>
        <div class="client-card-body">
          <div class="client-card-title"><h2>{{ client.platform }}</h2><span>{{ client.detail }}</span></div>
          <p>{{ client.description }}</p>
          <a class="button button-primary client-download-button" :href="client.href" :download="client.package">下载 {{ client.platform }} 版</a>
        </div>
      </article>
    </div>

    <p class="download-footnote">当前提供预览构建。Windows 文件尚未进行代码签名；Android 安装时可能需要允许浏览器或文件管理器安装应用。</p>
    <a class="release-history" href="https://github.com/Charlen2024/Nearlink/releases" target="_blank" rel="noreferrer">查看版本说明与更新记录</a>
  </section>
</template>
