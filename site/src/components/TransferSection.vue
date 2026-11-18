<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'

const scenarios = [
  { source: 'phone', target: 'computer', sourceLabel: '手机', targetLabel: '电脑', type: '文件传输', caption: '手机向电脑发送文件', bidirectional: false },
  { source: 'computer', target: 'phone', sourceLabel: '电脑', targetLabel: '手机', type: '文件传输', caption: '电脑向手机发送文件', bidirectional: false },
  { source: 'computer', target: 'computer', sourceLabel: '电脑', targetLabel: '电脑', type: '文件传输', caption: '电脑与电脑之间直传文件', bidirectional: false },
  { source: 'phone', target: 'phone', sourceLabel: '手机', targetLabel: '手机', type: '文件传输', caption: '手机与手机之间直传文件', bidirectional: false },
  { source: 'phone', target: 'computer', sourceLabel: '手机', targetLabel: '电脑', type: '双向消息', caption: '消息可以双向收发，任一设备都能发起', bidirectional: true },
]

const activeScenario = ref(0)
const scenario = computed(() => scenarios[activeScenario.value])
let rotationTimer: number | undefined

onMounted(() => {
  if (!window.matchMedia('(prefers-reduced-motion: reduce)').matches) {
    rotationTimer = window.setInterval(() => {
      activeScenario.value = (activeScenario.value + 1) % scenarios.length
    }, 4200)
  }
})

onUnmounted(() => {
  if (rotationTimer !== undefined) window.clearInterval(rotationTimer)
})
</script>

<template>
  <section class="transfer-section" id="how-it-works">
    <div class="transfer-copy">
      <p class="eyebrow">文件直达设备</p>
      <h2>不经过云端，<br />从一台设备传到另一台。</h2>
      <p>手机、平板和电脑连在同一局域网时，可以直接互传消息和文件。收到的文件保存在接收设备，你可以在会话里预览或保存。</p>
      <details id="privacy" class="privacy-note">
        <summary>隐私与安全说明</summary>
        <p>当前版本的设备间传输尚未提供端到端加密。传输敏感文件时，请使用可信的局域网，并确认连接的是自己的设备。</p>
      </details>
    </div>
    <div class="transfer-explainer">
      <div class="transfer-visual" role="img" :aria-label="`${scenario.caption}，通过同一局域网完成`">
        <Transition name="device-switch" mode="out-in">
          <span :key="scenario.source" class="transfer-device transfer-source" :class="`transfer-${scenario.source}`">
            <svg v-if="scenario.source === 'phone'" viewBox="0 0 48 76" aria-hidden="true"><rect x="6" y="2" width="36" height="72" rx="7"/><path d="M18 8h12M21 67h6"/><rect x="10" y="14" width="28" height="46" rx="2"/></svg>
            <svg v-else viewBox="0 0 88 68" aria-hidden="true"><rect x="3" y="3" width="82" height="49" rx="5"/><path d="M29 64h30M38 52v12M50 52v12"/><rect x="9" y="9" width="70" height="37" rx="2"/></svg>
            <small>{{ scenario.sourceLabel }}</small>
          </span>
        </Transition>
        <div class="transfer-lane" aria-hidden="true">
          <span class="lan-label"><i></i>同一局域网</span>
          <span class="lan-line"></span>
          <span class="transfer-package transfer-package-forward" :class="{ 'transfer-package-message': scenario.bidirectional }">
            <svg v-if="scenario.bidirectional" viewBox="0 0 24 24"><path d="M4 5h16v12H9l-5 3zM7 9h10M7 12h7"/></svg>
            <svg v-else viewBox="0 0 24 24"><path d="M6 3h8l4 4v14H6zM14 3v5h5M9 13h6M9 17h6"/></svg>
          </span>
          <span v-if="scenario.bidirectional" class="transfer-package transfer-package-reverse transfer-package-message">
            <svg viewBox="0 0 24 24"><path d="M4 5h16v12H9l-5 3zM7 9h10M7 12h7"/></svg>
          </span>
        </div>
        <Transition name="device-switch" mode="out-in">
          <span :key="scenario.target" class="transfer-device transfer-target" :class="`transfer-${scenario.target}`">
            <svg v-if="scenario.target === 'phone'" viewBox="0 0 48 76" aria-hidden="true"><rect x="6" y="2" width="36" height="72" rx="7"/><path d="M18 8h12M21 67h6"/><rect x="10" y="14" width="28" height="46" rx="2"/></svg>
            <svg v-else viewBox="0 0 88 68" aria-hidden="true"><rect x="3" y="3" width="82" height="49" rx="5"/><path d="M29 64h30M38 52v12M50 52v12"/><rect x="9" y="9" width="70" height="37" rx="2"/></svg>
            <small>{{ scenario.targetLabel }}</small>
          </span>
        </Transition>
      </div>
      <p class="transfer-visual-caption" aria-live="polite"><span>{{ scenario.type }}</span>{{ scenario.caption }}</p>
      <div class="transfer-facts" aria-label="文件传输流程">
      <div><span>第一步</span><b>选好设备，找到接收方</b><i>01</i></div>
      <div><span>第二步</span><b>消息和文件在局域网内直传</b><i>02</i></div>
      <div><span>第三步</span><b>在会话里预览，或保存到本地</b><i>03</i></div>
      </div>
    </div>
  </section>
</template>
