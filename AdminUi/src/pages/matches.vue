<template>

<main class="page">
    <div id="banner-host"></div>
    <div class="page-head">
        <h1>对局监控</h1>
        <p id="summary" class="muted"></p>
    </div>

    <div class="card">
        <h2>进行中的对局</h2>
        <div class="table-wrap" id="matches"></div>
    </div>

    <div class="card">
        <h2>匹配队列</h2>
        <div class="table-wrap" id="queues"></div>
        <div class="btn-row" style="margin-top:14px">
            <button class="btn btn--outline" id="clear-queues">清空全部队列</button>
            <button class="btn btn--outline" id="refresh">刷新</button>
        </div>
    </div>

</main>
</template>
<script setup>
import { onMounted } from 'vue';
onMounted(async () => {

  window.Admin.hydrateIcons();
  await import('../legacy/matches.js');
});
</script>
