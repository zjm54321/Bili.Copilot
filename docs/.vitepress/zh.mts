import { defineConfig, type DefaultTheme } from 'vitepress'

export const zh = defineConfig({
    lang: 'zh-Hans',
    title: '哔哩助理',
    themeConfig: {
        nav: [
          { text: '下载应用', link: 'https://www.microsoft.com/store/productId/9MVFJLPH517M' },
          { text: '播放体验', link: '/player' },
          { text: '常见问题', link: '/faq' },
        ],
    
        sidebar: [
          {
            text: '概览',
            items: [
              { text: '关于项目', link: '/about' },
              { text: '安装应用', link: '/install' },
              { text: '账户登录', link: '/sign-in' },
              { text: '模块分类', link: '/partition' },
              { text: '常见问题', link: './faq' },
              { text: '隐私策略', link: '/privacy' }
            ]
          },
          {
            text: '播放',
            items: [
              { text: '播放器类型', link: '/player' },
              { text: '自定义操作', link: '/player-custom' },
              { text: '弹幕体验', link: '/danmaku' },
              { text: 'WebDAV', link: '/webdav' },
            ]
          },
        ],

        editLink: {
            pattern: 'https://github.com/Richasy/Bili.Copilot/edit/main/docs/:path',
            text: '在 GitHub 上编辑此页面'
          },
      
          footer: {
            message: '基于 GPLv3 许可发布',
            copyright: `版权所有 © 2024-${new Date().getFullYear()} 云之幻`
          },
      
          docFooter: {
            prev: '上一页',
            next: '下一页'
          },
      
          outline: {
            label: '页面导航'
          },
      
          lastUpdated: {
            text: '最后更新于',
            formatOptions: {
              dateStyle: 'short',
              timeStyle: 'medium'
            }
          },
          langMenuLabel: '多语言',
          returnToTopLabel: '回到顶部',
          sidebarMenuLabel: '菜单',
          darkModeSwitchLabel: '主题',
          lightModeSwitchTitle: '切换到浅色模式',
          darkModeSwitchTitle: '切换到深色模式'
      }
})