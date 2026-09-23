<template>

<main class="page">
    <div id="banner-host"></div>
    <div class="page-head">
        <h1>用户管理</h1>
        <p>查看玩家资料、卡组和在线状态，或管理封禁与连接。</p>
    </div>

    <form class="search-row" id="search-form">
        <div class="field">
            <label for="q">搜索（ID / 昵称）</label>
            <input type="text" id="q" placeholder="例如 1001 或 player" />
        </div>
        <button class="btn" type="submit">搜索</button>
        <a class="btn btn--outline" href="/admin-ui/users.html">清除</a>
    </form>

    <div class="card"><div class="table-wrap" id="user-table"></div></div>

</main>
</template>
<script setup>
import { onMounted } from 'vue';
onMounted(async () => {

  window.Admin.hydrateIcons();
  await import('../legacy/users.js');
});
</script>
