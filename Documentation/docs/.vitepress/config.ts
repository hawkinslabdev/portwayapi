import { defineConfig } from 'vitepress';
import { withMermaid } from "vitepress-plugin-mermaid";
import footnote from 'markdown-it-footnote';
import taskLists from 'markdown-it-task-checkbox';

export default withMermaid(
  defineConfig({
  title: 'Portway',
  description: 'A lightweight API gateway for Windows environments',
  head: [['link', { rel: 'icon', href: '/favicon.ico' }]],
  themeConfig: {
    logo: '/logo.svg',
    nav: [
      { text: 'Home', link: '/' },
      { text: 'Guide', link: '/guide/' },
      { text: 'Reference', link: '/reference/' },
      {
        text: 'More',
        items: [
          { text: 'Download', link: 'https://github.com/melosso/portwayapi/releases/' },
          { text: 'Demo page', link: 'https://portway-demo.melosso.nl/' }
        ]
      }
      
    ],
    sidebar: {
      '/guide/': [
        {
          text: 'Introduction',
          items: [
            { text: 'What is Portway?', link: '/guide/' },
            { text: 'Getting Started', link: '/guide/getting-started' },
            { text: 'Structure', link: '/guide/routing' },
            { text: 'Deploying', link: '/guide/deployment' }
          ]
        },
        {
          text: 'Endpoints',
          items: [
            { text: 'SQL', link: '/guide/endpoints-sql' },
            { text: 'Proxy', link: '/guide/endpoints-proxy' },
            { text: 'Composite', link: '/guide/endpoints-composite' },
            { text: 'File System', link: '/guide/endpoints-file' },
            { text: 'Webhook', link: '/guide/endpoints-webhook' }            
          ]
        },
        {
          text: 'Configuration',
          items: [
            { text: 'Environments', link: '/guide/environments' },
            { text: 'Security', link: '/guide/security' },
            { text: 'Rate Limiting', link: '/guide/rate-limiting' },
            { text: 'Licensing', link: '/guide/licensing' }

          ]
        },
        {
          text: 'Operations',
          items: [
            { text: 'Monitoring', link: '/guide/monitoring' },
            { text: 'Troubleshooting', link: '/guide/troubleshooting' }
          ]
        },
        {
          text: 'Contributing',
          items: [
            { text: 'Bugs', link: 'https://github.com/melosso/portwayapi/issues' },
            { text: 'Suggestions', link: 'https://github.com/melosso/portwayapi/discussions/' },
          ]
        },
        {
           text: 'Coding & API Reference', link: '/reference/' 
        },
        {
          text: 'Plugins', link: '/reference/plugins/' 
        }
      ],
      '/reference/': [
        {
          text: 'API Reference',
          items: [
            { text: 'Overview', link: '/reference/' },
            { text: 'Authentication', link: '/reference/authentication' },
            { text: 'HTTP Headers', link: '/reference/headers' }
          ]
        },
        {
          text: 'Configuration Files',
          items: [
            { text: 'Entity Configuration', link: '/reference/entity-config' },
            { text: 'Environment Settings', link: '/reference/environment-settings' },
            { text: 'Application Settings', link: '/reference/app-settings' }
          ]
        },
        {
          text: 'Query Language',
          items: [
            { text: 'OData Syntax', link: '/reference/odata' },
            { text: 'Filter Operations', link: '/reference/filters' },
            { text: 'Sorting & Pagination', link: '/reference/sorting-pagination' }
          ]
        },
        {
          text: 'Tools & Options',
          items: [
            { text: 'Token Generator', link: '/reference/token-generator' },
            { text: 'Health Checks', link: '/reference/health-checks' },
            { text: 'Caching', link: '/reference/caching' },
            { text: 'Logging', link: '/reference/logging' },
            { text: 'Audit', link: '/reference/audit' }
          ]
        },
        {
          text: 'Plugins',
          items: [
            { text: 'Exact Globe+', link: '/reference/plugins/exact-globe' }
          ]
        }
      ]
    },
    socialLinks: [
      { icon: 'github', link: 'https://github.com/melosso/portwayapi' }
    ],
    search: {
      provider: 'local'
    },
    footer: {
      message: 'Released under the GNU AGPL (3.0) License.',
      copyright: 'Copyright © 2025-Present Melosso.com'
    }
  },
  vite: {
    server: {
      host: true, // This allows external access
      allowedHosts: ['localhost', '0.0.0.0', 'portway-docs.melosso.com']
    }
  },
  markdown: {
    config(md) {
      md.use(footnote);
      md.use(taskLists, {
        disabled: true,
        divWrap: false,
        divClass: 'checkbox',
        idPrefix: 'cbx_',
        ulClass: 'task-list',
        liClass: 'task-list-item',
      });
    }
  }
})
);
