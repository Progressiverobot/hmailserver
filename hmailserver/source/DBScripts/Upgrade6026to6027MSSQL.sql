ALTER TABLE hm_domains ADD domainmessageretentiondays int not null CONSTRAINT df_domainmessageretentiondays DEFAULT 0

ALTER TABLE hm_accounts ADD accountmessageretentiondays int not null CONSTRAINT df_accountmessageretentiondays DEFAULT 0

update hm_dbversion set value = 6027
