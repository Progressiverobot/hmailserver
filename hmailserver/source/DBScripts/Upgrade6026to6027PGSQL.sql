alter table hm_domains add column domainmessageretentiondays int not null default 0;

alter table hm_accounts add column accountmessageretentiondays int not null default 0;

update hm_dbversion set value = 6027;
