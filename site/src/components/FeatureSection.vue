<script setup lang="ts">
defineProps<{
  features: Array<{ number: string; title: string; description: string; symbol: string }>
}>()
</script>

<template>
  <section class="features section" id="features">
    <div class="section-heading">
      <p class="eyebrow">用起来很简单</p>
      <h2>把设备间的小事，<br />放在一个地方。</h2>
      <p class="section-intro">Nearlink 围绕日常互传设计：设备容易辨认，收到的内容有去处，文件状态看得见。</p>
    </div>
    <div class="feature-list">
      <article v-for="feature in features" :key="feature.number" class="feature-row">
        <span class="feature-number">{{ feature.number }}</span>
        <div><h3>{{ feature.title }}</h3><p>{{ feature.description }}</p></div>
        <span class="feature-symbol" aria-hidden="true">{{ feature.symbol }}</span>
      </article>
    </div>
  </section>
</template>
