alter table hm_rule_criterias alter column criteriamatchvalue type varchar(2000);

alter table hm_accounts add column accountvacationbegindate timestamp not null default '2001-01-01 00:00:00';

update hm_dbversion set value = 6012;
